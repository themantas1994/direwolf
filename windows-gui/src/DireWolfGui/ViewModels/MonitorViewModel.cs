using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Packets;
using DireWolfGui.Core.Process;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>Per-channel counters shown above the packet list.</summary>
public sealed class ChannelStripItem : ObservableObject
{
    public ChannelStripItem(int channel) => Channel = channel;

    public int Channel { get; }
    public string Title => $"Channel {Channel}";

    private string _counts = "";
    public string Counts { get => _counts; private set => Set(ref _counts, value); }

    private string _audio = "";
    public string Audio { get => _audio; private set => Set(ref _audio, value); }

    private string _lastHeard = "";
    public string LastHeard { get => _lastHeard; private set => Set(ref _lastHeard, value); }

    public void Update(ChannelActivity a, DateTimeOffset now)
    {
        Counts = $"{a.Received:N0} rx · {a.Transmitted:N0} tx";
        Audio = a.LastAudioLevel is int l ? $"audio level {l}" : "audio level —";
        LastHeard = a.LastReceived is { } t ? "last rx " + Converters.AgeConverter.Format(now - t) + " ago" : "nothing received";
    }
}

/// <summary>"Packet monitor": live list of packets with filters, detail panel and exports.</summary>
public sealed class MonitorViewModel : PageViewModel
{
    private const int MaxPerTick = 1000;
    private readonly IShell _shell;
    private readonly List<PacketRecord> _all = new();   // everything kept (retention limit), oldest first
    private readonly Dictionary<int, ChannelStripItem> _channelItems = new();
    private long _lastSeq;
    private long _missed;
    private DateTimeOffset _lastStripUpdate;
    private PacketFilterCriteria _criteria = new();
    private int _detailDecodedCount = -1;
    private bool _detailHadRaw;

    public MonitorViewModel(IShell shell) : base("Packet monitor", "", "Ctrl+2")
    {
        _shell = shell;

        ChannelChoices.Add(new FilterChoice<int?>("All channels", null));
        ChannelChoices.Add(new FilterChoice<int?>("No channel (APRS-IS)", -1));
        for (int i = 0; i < 2; i++) ChannelChoices.Add(new FilterChoice<int?>($"Channel {i}", i));
        _channel = ChannelChoices[0];

        OriginChoices =
        [
            new("All directions and origins", (null, null)),
            new("Received (any origin)", (PacketDirection.Received, null)),
            new("Transmitted (any)", (PacketDirection.Transmitted, null)),
            new("RF received", (PacketDirection.Received, PacketOrigin.Radio)),
            new("Transmitted by this station", (PacketDirection.Transmitted, PacketOrigin.Local)),
            new("From APRS-IS", (null, PacketOrigin.FromAprsIs)),
            new("To APRS-IS", (null, PacketOrigin.ToAprsIs)),
            new("IGate → RF", (null, PacketOrigin.IgateToRadio)),
            new("WAPR gateway relayed", (null, PacketOrigin.Gateway)),
            new("Network TNC client", (null, PacketOrigin.NetworkTnc)),
            new("DTMF", (null, PacketOrigin.Dtmf)),
            new("AIS", (null, PacketOrigin.Ais)),
        ];
        _origin = OriginChoices[0];

        TypeChoices.Add(new("All packet types", (null, false)));
        TypeChoices.Add(new("Not APRS (AX.25 frames)", (null, true)));
        foreach (var t in Enum.GetValues<AprsPacketType>())
            TypeChoices.Add(new("APRS: " + PacketOriginText.AprsTypeText(t), (t, false)));
        _type = TypeChoices[0];

        PauseCommand = new RelayCommand(() => IsPaused = !IsPaused);
        ClearCommand = new RelayCommand(ClearView);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => !_criteria.IsEmpty);
        CopyPacketCommand = new RelayCommand(CopyPacket, () => SelectedPacket != null);
        CopyCallsignCommand = new RelayCommand(_ => CopyCallsign(), _ => SelectedPacket != null);
        CopyRowsCommand = new RelayCommand(p => CopyRows(p as IList), p => p is IList { Count: > 0 });
        ExportVisibleCommand = new AsyncCommand(() => ExportAsync(Packets.ToList(), "visible"), () => Packets.Count > 0);
        ExportSelectedCommand = new AsyncCommand(p => ExportAsync((p as IList)?.OfType<PacketItemViewModel>().ToList() ?? [], "selected"),
            p => p is IList { Count: > 0 });
        JumpToLatestCommand = new RelayCommand(() => ScrollToEndRequested?.Invoke(this, EventArgs.Empty));
    }

    public BatchObservableCollection<PacketItemViewModel> Packets { get; } = new();
    public ObservableCollection<ChannelStripItem> Channels { get; } = new();

    /// <summary>Raised after new rows were appended and auto-scroll is on, or on request.</summary>
    public event EventHandler? ScrollToEndRequested;

    public ICommand PauseCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand CopyPacketCommand { get; }
    public ICommand CopyCallsignCommand { get; }
    public ICommand CopyRowsCommand { get; }
    public ICommand ExportVisibleCommand { get; }
    public ICommand ExportSelectedCommand { get; }
    public ICommand JumpToLatestCommand { get; }

    // ---- State ----

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (!Set(ref _isPaused, value)) return;
            OnPropertyChanged(nameof(PauseButtonText));
            OnPropertyChanged(nameof(PauseGlyph));
            if (!value) Tick();
            UpdateStatusText();
        }
    }

    public string PauseButtonText => IsPaused ? "Resume" : "Pause";
    public string PauseGlyph => IsPaused ? "" : "";

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }

    private string _pendingText = "";
    /// <summary>"N new" while paused, plus a note when packets were dropped by the station's buffer.</summary>
    public string PendingText { get => _pendingText; private set => Set(ref _pendingText, value); }

    private string _countText = "";
    public string CountText { get => _countText; private set => Set(ref _countText, value); }

    private string _emptyText = "";
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    public int RetentionLimit => Math.Clamp(_shell.Settings.PacketRetentionCount, 100, 1_000_000);

    // ---- Filters ----

    public ObservableCollection<FilterChoice<int?>> ChannelChoices { get; } = new();
    public IReadOnlyList<FilterChoice<(PacketDirection? Direction, PacketOrigin? Origin)>> OriginChoices { get; }
    public List<FilterChoice<(AprsPacketType? Type, bool NonAprs)>> TypeChoices { get; } = new();

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) ApplyFilter(); } }

    private string _callsign = "";
    public string Callsign { get => _callsign; set { if (Set(ref _callsign, value)) ApplyFilter(); } }

    private FilterChoice<int?> _channel;
    public FilterChoice<int?> SelectedChannel { get => _channel; set { if (value != null && Set(ref _channel, value)) ApplyFilter(); } }

    private FilterChoice<(PacketDirection? Direction, PacketOrigin? Origin)> _origin;
    public FilterChoice<(PacketDirection? Direction, PacketOrigin? Origin)> SelectedOrigin
    { get => _origin; set { if (value != null && Set(ref _origin, value)) ApplyFilter(); } }

    private FilterChoice<(AprsPacketType? Type, bool NonAprs)> _type;
    public FilterChoice<(AprsPacketType? Type, bool NonAprs)> SelectedType
    { get => _type; set { if (value != null && Set(ref _type, value)) ApplyFilter(); } }

    private bool _waprOnly;
    public bool WaprOnly { get => _waprOnly; set { if (Set(ref _waprOnly, value)) ApplyFilter(); } }

    public bool IsFiltered => !_criteria.IsEmpty;

    private void ApplyFilter()
    {
        _criteria = new PacketFilterCriteria
        {
            Search = Search,
            Callsign = Callsign,
            Channel = _channel.Value is >= 0 ? _channel.Value : null,
            NoChannelOnly = _channel.Value == -1,
            Direction = _origin.Value.Direction,
            Origin = _origin.Value.Origin,
            AprsType = _type.Value.Type,
            NonAprsOnly = _type.Value.NonAprs,
            WaprOnly = WaprOnly,
        };
        OnPropertyChanged(nameof(IsFiltered));
        var keep = SelectedPacket;
        Packets.ReplaceAll(_all.Where(p => PacketFilter.Matches(p, _criteria)).Select(p => new PacketItemViewModel(p)));
        if (keep != null) SelectedPacket = Packets.FirstOrDefault(x => ReferenceEquals(x.Packet, keep.Packet));
        UpdateStatusText();
        if (AutoScroll) ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClearFilters()
    {
        _search = ""; _callsign = ""; _channel = ChannelChoices[0]; _origin = OriginChoices[0]; _type = TypeChoices[0]; _waprOnly = false;
        foreach (var n in new[] { nameof(Search), nameof(Callsign), nameof(SelectedChannel), nameof(SelectedOrigin), nameof(SelectedType), nameof(WaprOnly) })
            OnPropertyChanged(n);
        ApplyFilter();
    }

    // ---- Polling ----

    protected override void OnShown()
    {
        // Cheap catch-up: when far behind (page was hidden during heavy traffic) jump to the latest packets.
        long last = _shell.Session.Packets.LastSequence;
        if (last - _lastSeq > MaxPerTick * 2 && !IsPaused)
        {
            var latest = _shell.Session.Packets.GetLatest(RetentionLimit);
            if (latest.Count > 0 && _lastSeq > 0 && latest[0].Sequence > _lastSeq + 1) _missed += latest[0].Sequence - _lastSeq - 1;
            _all.Clear();
            _all.AddRange(latest);
            _lastSeq = latest.Count > 0 ? latest[^1].Sequence : last;
            ApplyFilter();
        }
        Tick();
    }

    public override void Tick()
    {
        var store = _shell.Session.Packets;
        if (!IsPaused)
        {
            var batch = store.GetSince(_lastSeq, MaxPerTick);
            if (batch.Count > 0)
            {
                if (batch[0].Sequence > _lastSeq + 1 && _lastSeq > 0) _missed += batch[0].Sequence - _lastSeq - 1;
                _lastSeq = batch[^1].Sequence;
                Append(batch);
            }
        }
        var now = DateTimeOffset.Now;
        if (now - _lastStripUpdate > TimeSpan.FromSeconds(1))
        {
            _lastStripUpdate = now;
            UpdateChannels(now);
        }
        RefreshDetailIfChanged();
        UpdateStatusText();
    }

    private void Append(IReadOnlyList<PacketRecord> batch)
    {
        _all.AddRange(batch);
        int limit = RetentionLimit;
        int slack = Math.Max(100, limit / 10);
        if (_all.Count > limit + slack)
        {
            int drop = _all.Count - limit;
            var cutoff = _all[drop - 1].Sequence;
            _all.RemoveRange(0, drop);
            int visibleDrop = 0;
            while (visibleDrop < Packets.Count && Packets[visibleDrop].Sequence <= cutoff) visibleDrop++;
            var keep = _selected;
            Packets.RemoveFirst(visibleDrop);
            KeepSelection(keep);
        }
        var add = new List<PacketItemViewModel>(batch.Count);
        foreach (var p in batch) if (PacketFilter.Matches(p, _criteria)) add.Add(new PacketItemViewModel(p));
        if (add.Count > 0)
        {
            var keep = _selected;
            Packets.AddRange(add);
            KeepSelection(keep);
            if (AutoScroll) ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateChannels(DateTimeOffset now)
    {
        foreach (var a in _shell.Session.ChannelActivity)
        {
            if (!_channelItems.TryGetValue(a.Channel, out var item))
            {
                item = new ChannelStripItem(a.Channel);
                _channelItems[a.Channel] = item;
                int idx = 0;
                while (idx < Channels.Count && Channels[idx].Channel < a.Channel) idx++;
                Channels.Insert(idx, item);
                if (!ChannelChoices.Any(c => c.Value == a.Channel))
                {
                    int ci = 2;
                    while (ci < ChannelChoices.Count && ChannelChoices[ci].Value < a.Channel) ci++;
                    ChannelChoices.Insert(ci, new FilterChoice<int?>($"Channel {a.Channel}", a.Channel));
                }
            }
            item.Update(a, now);
        }
    }

    private void UpdateStatusText()
    {
        long pending = IsPaused ? Math.Max(0, _shell.Session.Packets.LastSequence - _lastSeq) : 0;
        var sb = new StringBuilder();
        if (IsPaused) sb.Append(pending == 0 ? "Paused — no new packets" : $"Paused — {pending:N0} new");
        if (_missed > 0)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append($"{_missed:N0} packets were not shown (they left the station's packet buffer while paused or hidden)");
        }
        PendingText = sb.ToString();

        CountText = IsFiltered
            ? $"{Packets.Count:N0} of {_all.Count:N0} packets match the filters"
            : $"{_all.Count:N0} packets (keeps the latest {RetentionLimit:N0})";

        string empty = "";
        if (Packets.Count == 0)
        {
            var state = _shell.Session.State;
            if (_all.Count > 0) empty = "No packets match the filters.";
            else if (state is DireWolfState.Stopped or DireWolfState.Failed) empty = "Dire Wolf is not running — press Start (F5).";
            else if (state == DireWolfState.Starting) empty = "Dire Wolf is starting…";
            else empty = "Listening… no packets yet. Received and transmitted packets appear here as Dire Wolf reports them.";
        }
        EmptyText = empty;
    }

    private void ClearView()
    {
        _all.Clear();
        _missed = 0;
        _lastSeq = _shell.Session.Packets.LastSequence;
        SelectedPacket = null;
        Packets.ReplaceAll([]);
        UpdateStatusText();
    }

    // ---- Selection and detail ----

    private PacketItemViewModel? _selected;
    public PacketItemViewModel? SelectedPacket
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            BuildDetail();
        }
    }

    /// <summary>A batch Reset makes the grid drop its selection; put it back if the row is still there.</summary>
    private void KeepSelection(PacketItemViewModel? keep)
    {
        if (keep == null || ReferenceEquals(_selected, keep)) return;
        if (_selected == null && Packets.Contains(keep)) SelectedPacket = keep;
    }

    private string _detailSummary = "";
    public string DetailSummary { get => _detailSummary; private set => Set(ref _detailSummary, value); }

    private string _detailDecoded = "";
    public string DetailDecoded { get => _detailDecoded; private set => Set(ref _detailDecoded, value); }

    private string _detailAprs = "";
    public string DetailAprs { get => _detailAprs; private set => Set(ref _detailAprs, value); }

    private string _detailRaw = "";
    public string DetailRaw { get => _detailRaw; private set => Set(ref _detailRaw, value); }

    private string _detailHex = "";
    public string DetailHex { get => _detailHex; private set => Set(ref _detailHex, value); }

    private string _detailAx25 = "";
    public string DetailAx25 { get => _detailAx25; private set => Set(ref _detailAx25, value); }

    public bool HasSelection => _selected != null;

    private void RefreshDetailIfChanged()
    {
        if (_selected is not { } s) return;
        if (s.Packet.DecodedLines.Count != _detailDecodedCount || (s.Packet.RawFrame != null) != _detailHadRaw) BuildDetail();
    }

    private void BuildDetail()
    {
        OnPropertyChanged(nameof(HasSelection));
        if (_selected is not { } item)
        {
            DetailSummary = DetailDecoded = DetailAprs = DetailRaw = DetailHex = DetailAx25 = "";
            return;
        }
        var p = item.Packet;
        _detailDecodedCount = p.DecodedLines.Count;
        _detailHadRaw = p.RawFrame != null;

        var sb = new StringBuilder();
        void Line(string label, string? value) { if (!string.IsNullOrEmpty(value)) sb.Append(label.PadRight(13)).Append(value).Append('\n'); }
        Line("Time", p.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture));
        Line("Origin", item.OriginText + " — " + item.OriginToolTip);
        Line("Channel", p.Channel?.ToString(CultureInfo.InvariantCulture) is { } c ? $"{c}  [{p.Label}]" : $"none  [{p.Label}]");
        Line("Source", p.Source);
        Line("Destination", p.Destination);
        Line("Path", p.PathText.Length > 0 ? p.PathText : "(none)");
        Line("Heard", p.Heard is { } h && !string.Equals(h, p.Source, StringComparison.OrdinalIgnoreCase) ? h + " (digipeater heard directly)" : p.Heard);
        Line("Audio level", p.AudioLevelText);
        Line("FEC / mode", p.Fec);
        Line("WAPR", p.IsWapr ? (p.WaprDetails ?? "yes") : null);
        Line("Frame", p.FrameDescription);
        Line("Type", item.TypeText);
        Line("Info", PacketOriginText.OneLine(p.Info, 2000));
        Line("Reported by", p.SourceKind switch
        {
            PacketSourceKind.Console => "Dire Wolf console output",
            PacketSourceKind.AgwMonitor => "AGW monitor",
            _ => "KISS",
        });
        DetailSummary = sb.ToString().TrimEnd();

        DetailDecoded = p.DecodedLines.Count > 0 ? string.Join("\n", p.DecodedLines) : "(Dire Wolf printed no decode lines for this packet)";
        DetailAprs = p.Aprs is { } a ? DescribeAprs(a) : p.FrameDescription != null ? "Not an APRS frame (AX.25: " + p.FrameDescription + ")" : "(no APRS parse)";
        DetailRaw = p.RawText;
        if (p.RawFrame is { } raw)
        {
            DetailHex = HexDump.Format(raw);
            DetailAx25 = HexDump.DescribeAx25(raw);
        }
        else
        {
            DetailHex = p.Direction == PacketDirection.Transmitted
                ? "Raw bytes not available — raw frames are only captured for received packets."
                : _shell.Session.AgwMonitorReady
                    ? "Raw bytes not available for this packet (no matching frame from the AGW monitor)."
                    : "Raw bytes not available — AGW monitor not connected.";
            DetailAx25 = "";
        }
    }

    public static string DescribeAprs(AprsInfo a)
    {
        var sb = new StringBuilder();
        void Line(string label, string? value) { if (!string.IsNullOrEmpty(value)) sb.Append(label.PadRight(13)).Append(value).Append('\n'); }
        var inv = CultureInfo.InvariantCulture;
        Line("APRS type", PacketOriginText.AprsTypeText(a.Type) + (a.DataType != '\0' ? $"  (data type '{a.DataType}')" : ""));
        if (a.HasPosition)
        {
            Line("Latitude", a.Latitude!.Value.ToString("0.00000", inv));
            Line("Longitude", a.Longitude!.Value.ToString("0.00000", inv));
            if (a.PositionAmbiguity > 0) Line("Ambiguity", $"{a.PositionAmbiguity} digit(s) blanked — position is approximate");
            Line("Format", a.Compressed ? "compressed" : "plain");
        }
        Line("Symbol", a.Symbol);
        Line("Course", a.CourseDegrees?.ToString(inv) + (a.CourseDegrees != null ? "°" : ""));
        Line("Speed", a.SpeedKnots is double sp ? $"{sp:0.#} kn ({sp * 1.852:0.#} km/h)" : null);
        Line("Altitude", a.AltitudeFeet is double alt ? $"{alt:0} ft ({alt * 0.3048:0} m)" : null);
        Line("Timestamp", a.Timestamp);
        Line("Object", a.ObjectName is { } n ? n.Trim() + (a.ObjectAlive == false ? " (killed)" : "") : null);
        Line("Addressee", a.Addressee?.Trim());
        Line("Message", a.MessageText);
        Line("Message id", a.MessageId);
        Line("Reply-ack", a.ReplyAck);
        Line("Status", a.StatusText);
        Line("Mic-E", a.MicEMessage);
        Line("Third party", a.ThirdPartyPayload);
        Line("Comment", a.Comment is { } cm ? PacketOriginText.OneLine(cm, 2000) : null);
        Line("Problem", a.Error);
        return sb.ToString().TrimEnd();
    }

    // ---- Copy / export ----

    private void CopyPacket()
    {
        if (_selected is not { } s) return;
        var text = s.ToTextLine();
        if (s.Packet.DecodedLines.Count > 0) text += "\n" + string.Join("\n", s.Packet.DecodedLines);
        Report(ClipboardHelper.TrySetText(text), "Packet copied to the clipboard.");
    }

    private void CopyCallsign()
    {
        if (_selected is not { } s) return;
        Report(ClipboardHelper.TrySetText(s.Source), $"Copied {s.Source}.");
    }

    private void CopyRows(IList? rows)
    {
        var items = rows?.OfType<PacketItemViewModel>().OrderBy(r => r.Sequence).ToList();
        if (items is not { Count: > 0 }) return;
        Report(ClipboardHelper.TrySetText(string.Join(Environment.NewLine, items.Select(i => i.ToTextLine()))),
            $"Copied {items.Count:N0} packet(s).");
    }

    private void Report(bool ok, string message) =>
        _shell.SetStatus(ok ? message : "Could not use the clipboard (another application may be using it). Try again.");

    private async Task ExportAsync(IReadOnlyList<PacketItemViewModel> rows, string what)
    {
        if (rows.Count == 0) return;
        var path = Dialogs.SaveFile("CSV file (*.csv)|*.csv|Plain text (*.txt)|*.txt",
            $"packets-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path == null) return;
        var records = rows.OrderBy(r => r.Sequence).ToList();
        bool text = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        await Task.Run(() =>
        {
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            if (text) foreach (var r in records) w.WriteLine(r.ToTextLine());
            else Exporters.WritePacketsCsv(records.Select(r => r.Packet), w);
        });
        _shell.SetStatus($"Exported {records.Count:N0} {what} packet(s) to {path}");
    }
}
