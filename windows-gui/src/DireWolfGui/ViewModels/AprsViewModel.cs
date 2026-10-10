using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using DireWolfGui.Controls;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Packets;
using DireWolfGui.Core.Process;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

public enum StationSort { LastHeard, Callsign, Distance }

/// <summary>"APRS map &amp; stations": stations heard (real data only), map, details and exports.</summary>
public sealed class AprsViewModel : PageViewModel
{
    private readonly IShell _shell;
    private readonly Dictionary<string, StationItemViewModel> _items = new(StringComparer.OrdinalIgnoreCase);
    private long _lastVersion = -1;
    private DateTimeOffset _lastRefresh;
    private bool _dirty = true;
    private bool _fittedOnce;
    private bool _syncingSelection;
    private StationFilterCriteria _criteria = new();
    private List<TrackedStation> _filtered = new();

    public AprsViewModel(IShell shell) : base("APRS map & stations", "", "Ctrl+3")
    {
        _shell = shell;

        AgeChoices =
        [
            new("Any age", null), new("Last 10 minutes", TimeSpan.FromMinutes(10)), new("Last 30 minutes", TimeSpan.FromMinutes(30)),
            new("Last hour", TimeSpan.FromHours(1)), new("Last 3 hours", TimeSpan.FromHours(3)), new("Last 12 hours", TimeSpan.FromHours(12)),
            new("Last 24 hours", TimeSpan.FromHours(24)),
        ];
        _age = AgeChoices[0];
        DistanceChoices =
        [
            new("Any distance", null), new("Within 10 km", 10), new("Within 25 km", 25), new("Within 50 km", 50),
            new("Within 100 km", 100), new("Within 250 km", 250), new("Within 500 km", 500),
        ];
        _distance = DistanceChoices[0];
        ChannelChoices.Add(new("All channels", null));
        ChannelChoices.Add(new("No channel (APRS-IS)", -1));
        _channel = ChannelChoices[0];
        KindChoices = [new("Stations and objects", StationKindFilter.All), new("Stations only", StationKindFilter.Stations), new("Objects and items only", StationKindFilter.Objects)];
        _kind = KindChoices[0];
        SortChoices = [new("Sort: last heard", StationSort.LastHeard), new("Sort: callsign", StationSort.Callsign), new("Sort: distance", StationSort.Distance)];
        _sort = SortChoices[0];

        FitAllCommand = new RelayCommand(() => FitRequested?.Invoke(this, MapMarkers), () => MapMarkers.Count > 0);
        CenterSelectedCommand = new RelayCommand(CenterOnSelected, () => SelectedStation?.Station.HasPosition == true);
        SendMessageCommand = new RelayCommand(SendMessage, () => MessageTarget != null);
        ExportStationsCommand = new AsyncCommand(ExportStationsAsync, () => _filtered.Count > 0);
        ExportGpxAllCommand = new AsyncCommand(() => ExportGpxAsync(_filtered, "all shown stations"), () => _filtered.Any(s => s.Track.Count > 0));
        ExportGpxSelectedCommand = new AsyncCommand(
            () => ExportGpxAsync(SelectedStation is { } s ? [s.Station] : [], SelectedStation?.Callsign ?? ""),
            () => SelectedStation?.Station.Track.Count > 0);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    public ObservableCollection<StationItemViewModel> Stations { get; } = new();

    private IReadOnlyList<MapMarker> _mapMarkers = [];
    /// <summary>Markers for listed stations with a real decoded position only.</summary>
    public IReadOnlyList<MapMarker> MapMarkers { get => _mapMarkers; private set => Set(ref _mapMarkers, value); }

    /// <summary>The view fits the map to these markers.</summary>
    public event EventHandler<IReadOnlyList<MapMarker>>? FitRequested;
    /// <summary>The view centres the map on this marker.</summary>
    public event EventHandler<MapMarker>? CenterRequested;
    /// <summary>The view scrolls the list to the selected station.</summary>
    public event EventHandler? SelectionScrollRequested;

    public ICommand FitAllCommand { get; }
    public ICommand CenterSelectedCommand { get; }
    public ICommand SendMessageCommand { get; }
    public ICommand ExportStationsCommand { get; }
    public ICommand ExportGpxAllCommand { get; }
    public ICommand ExportGpxSelectedCommand { get; }
    public ICommand ClearFiltersCommand { get; }

    // ---- Map settings (from AppSettings, read when the page is shown) ----

    private bool _tilesEnabled;
    public bool TilesEnabled { get => _tilesEnabled; private set => Set(ref _tilesEnabled, value); }
    private string _tileUrlTemplate = "";
    public string TileUrlTemplate { get => _tileUrlTemplate; private set => Set(ref _tileUrlTemplate, value); }
    private string _attribution = "";
    public string Attribution { get => _attribution; private set => Set(ref _attribution, value); }
    private string _tileCacheDirectory = "";
    public string TileCacheDirectory { get => _tileCacheDirectory; private set => Set(ref _tileCacheDirectory, value); }
    private double? _homeLatitude;
    public double? HomeLatitude { get => _homeLatitude; private set => Set(ref _homeLatitude, value); }
    private double? _homeLongitude;
    public double? HomeLongitude { get => _homeLongitude; private set => Set(ref _homeLongitude, value); }
    public bool HasHome => HomeLatitude.HasValue && HomeLongitude.HasValue;

    // Map view, kept here so it survives the view being recreated when switching pages.
    private double _mapCenterLatitude = 20;
    public double MapCenterLatitude { get => _mapCenterLatitude; set => Set(ref _mapCenterLatitude, value); }
    private double _mapCenterLongitude;
    public double MapCenterLongitude { get => _mapCenterLongitude; set => Set(ref _mapCenterLongitude, value); }
    private double _mapZoom = 2;
    public double MapZoom { get => _mapZoom; set => Set(ref _mapZoom, value); }

    private bool _showTracks = true;
    public bool ShowTracks { get => _showTracks; set => Set(ref _showTracks, value); }
    private bool _showLabels = true;
    public bool ShowLabels { get => _showLabels; set => Set(ref _showLabels, value); }

    private bool _csvLogHint;
    /// <summary>Dire Wolf's CSV log is off: positions only come from this application's parse of packets.</summary>
    public bool ShowCsvLogHint { get => _csvLogHint; private set => Set(ref _csvLogHint, value); }

    // ---- Filters ----

    public IReadOnlyList<FilterChoice<TimeSpan?>> AgeChoices { get; }
    public IReadOnlyList<FilterChoice<double?>> DistanceChoices { get; }
    public ObservableCollection<FilterChoice<int?>> ChannelChoices { get; } = new();
    public IReadOnlyList<FilterChoice<StationKindFilter>> KindChoices { get; }
    public IReadOnlyList<FilterChoice<StationSort>> SortChoices { get; }

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) FilterChanged(); } }
    private FilterChoice<TimeSpan?> _age;
    public FilterChoice<TimeSpan?> SelectedAge { get => _age; set { if (value != null && Set(ref _age, value)) FilterChanged(); } }
    private FilterChoice<double?> _distance;
    public FilterChoice<double?> SelectedDistance { get => _distance; set { if (value != null && Set(ref _distance, value)) FilterChanged(); } }
    private FilterChoice<int?> _channel;
    public FilterChoice<int?> SelectedChannel { get => _channel; set { if (value != null && Set(ref _channel, value)) FilterChanged(); } }
    private FilterChoice<StationKindFilter> _kind;
    public FilterChoice<StationKindFilter> SelectedKind { get => _kind; set { if (value != null && Set(ref _kind, value)) FilterChanged(); } }
    private FilterChoice<StationSort> _sort;
    public FilterChoice<StationSort> SelectedSort { get => _sort; set { if (value != null && Set(ref _sort, value)) FilterChanged(); } }
    private bool _hasPositionOnly;
    public bool HasPositionOnly { get => _hasPositionOnly; set { if (Set(ref _hasPositionOnly, value)) FilterChanged(); } }

    private void FilterChanged()
    {
        _criteria = new StationFilterCriteria
        {
            Search = Search,
            MaxAge = _age.Value,
            MaxDistanceKm = HasHome ? _distance.Value : null,
            Channel = _channel.Value is >= 0 ? _channel.Value : null,
            NoChannelOnly = _channel.Value == -1,
            Kind = _kind.Value,
            HasPositionOnly = HasPositionOnly,
        };
        _dirty = true;
        if (IsActive) Refresh();
    }

    private void ClearFilters()
    {
        _search = ""; _age = AgeChoices[0]; _distance = DistanceChoices[0]; _channel = ChannelChoices[0]; _kind = KindChoices[0]; _hasPositionOnly = false;
        foreach (var n in new[] { nameof(Search), nameof(SelectedAge), nameof(SelectedDistance), nameof(SelectedChannel), nameof(SelectedKind), nameof(HasPositionOnly) })
            OnPropertyChanged(n);
        FilterChanged();
    }

    // ---- Status texts ----

    private string _countText = "";
    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    private string _emptyText = "";
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }
    private string _mapEmptyText = "";
    public string MapEmptyText { get => _mapEmptyText; private set => Set(ref _mapEmptyText, value); }

    // ---- Polling ----

    protected override void OnShown()
    {
        var s = _shell.Settings;
        TilesEnabled = s.MapTilesEnabled;
        TileUrlTemplate = s.MapTileUrlTemplate;
        Attribution = s.MapAttribution;
        TileCacheDirectory = s.EffectiveTileCacheDirectory;
        bool homeChanged = HomeLatitude != s.HomeLatitude || HomeLongitude != s.HomeLongitude;
        HomeLatitude = s.HomeLatitude;
        HomeLongitude = s.HomeLongitude;
        OnPropertyChanged(nameof(HasHome));
        ShowCsvLogHint = !s.CsvLogEnabled;
        if (!_fittedOnce && HasHome)
        {
            MapCenterLatitude = HomeLatitude!.Value;
            MapCenterLongitude = HomeLongitude!.Value;
            MapZoom = 9;
        }
        if (homeChanged) FilterChanged();
        Refresh();
    }

    public override void Tick()
    {
        var now = DateTimeOffset.Now;
        long v = _shell.Session.Stations.Version;
        bool changed = v != _lastVersion;
        // New data at most once a second; otherwise every 10 s so ages and staleness stay true.
        if ((changed || _dirty) && now - _lastRefresh >= TimeSpan.FromSeconds(1) || now - _lastRefresh >= TimeSpan.FromSeconds(10))
            Refresh();
    }

    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        _lastRefresh = now;
        _lastVersion = _shell.Session.Stations.Version;
        _dirty = false;
        var all = _shell.Session.Stations.GetStations();
        double? hl = HomeLatitude, ho = HomeLongitude;

        foreach (var ch in all.Select(s => s.Channel).OfType<int>().Distinct())
        {
            if (ChannelChoices.Any(c => c.Value == ch)) continue;
            int i = 2;
            while (i < ChannelChoices.Count && ChannelChoices[i].Value < ch) i++;
            ChannelChoices.Insert(i, new FilterChoice<int?>($"Channel {ch}", ch));
        }

        var filtered = all.Where(s => StationFilter.Matches(s, _criteria, now, StationFilter.DistanceKm(s, hl, ho))).ToList();
        filtered = _sort.Value switch
        {
            StationSort.Callsign => filtered.OrderBy(s => s.Callsign, StringComparer.OrdinalIgnoreCase).ToList(),
            StationSort.Distance => filtered.OrderBy(s => StationFilter.DistanceKm(s, hl, ho) ?? double.MaxValue).ThenByDescending(s => s.LastHeard).ToList(),
            _ => filtered,   // GetStations is newest first
        };
        _filtered = filtered;

        // Sync the list in place (moves/inserts) so selection and scroll position survive.
        var wanted = new List<StationItemViewModel>(filtered.Count);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in filtered)
        {
            if (!_items.TryGetValue(s.Callsign, out var item))
            {
                item = new StationItemViewModel(s);
                _items[s.Callsign] = item;
            }
            item.Update(s, now, hl, ho);
            wanted.Add(item);
            keep.Add(s.Callsign);
        }
        foreach (var k in _items.Keys.Where(k => !keep.Contains(k)).ToList()) _items.Remove(k);
        var selected = SelectedStation;
        SyncList(wanted);
        if (selected != null && !Stations.Contains(selected)) SelectedStation = null;
        else if (selected != null && !ReferenceEquals(selected, SelectedStation)) SelectedStation = selected;

        var markers = new List<MapMarker>();
        foreach (var s in filtered)
        {
            if (!s.HasPosition) continue;
            var track = s.Track.Count > 1 ? s.Track.Take(s.Track.Count - 1).Select(t => new Point(t.Longitude, t.Latitude)).ToList() : [];
            markers.Add(new MapMarker
            {
                Id = s.Callsign, Label = s.Callsign, Latitude = s.Latitude!.Value, Longitude = s.Longitude!.Value, Track = track,
                IsStale = StationFilter.IsPositionStale(s, now), IsObject = s.IsObject, Symbol = s.Symbol,
            });
        }
        bool hadMarkers = MapMarkers.Count > 0;
        MapMarkers = markers;
        if (hadMarkers != markers.Count > 0) CommandManager.InvalidateRequerySuggested();
        if (!_fittedOnce && markers.Count > 0 && FitRequested != null)
        {
            _fittedOnce = true;
            FitRequested?.Invoke(this, markers);
        }

        int withPos = all.Count(s => s.HasPosition);
        CountText = $"{filtered.Count:N0} of {all.Count:N0} stations shown · {markers.Count:N0} on the map ({withPos:N0} known positions in total)";
        var state = _shell.Session.State;
        EmptyText = filtered.Count > 0 ? ""
            : all.Count > 0 ? "No stations match the filters."
            : state is DireWolfState.Stopped or DireWolfState.Failed ? "Dire Wolf is not running — press Start (F5)."
            : "No stations heard yet. Stations appear here when their packets are received.";
        MapEmptyText = markers.Count > 0 ? ""
            : "No positions to show. Only stations that sent a real, decodable position are placed on the map.";

        if (_selected != null) BuildDetail();
    }

    private void SyncList(List<StationItemViewModel> wanted)
    {
        for (int i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            if (i < Stations.Count && ReferenceEquals(Stations[i], w)) continue;
            int j = -1;
            for (int k = i + 1; k < Stations.Count; k++) if (ReferenceEquals(Stations[k], w)) { j = k; break; }
            if (j >= 0) Stations.Move(j, i);
            else Stations.Insert(i, w);
        }
        while (Stations.Count > wanted.Count) Stations.RemoveAt(Stations.Count - 1);
    }

    // ---- Selection ----

    private StationItemViewModel? _selected;
    public StationItemViewModel? SelectedStation
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (!_syncingSelection)
            {
                _syncingSelection = true;
                SelectedMapId = value?.Id;
                _syncingSelection = false;
            }
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(MessageTarget));
            OnPropertyChanged(nameof(SendMessageText));
            BuildDetail();
        }
    }

    private string? _selectedMapId;
    /// <summary>Two-way with the map's SelectedId.</summary>
    public string? SelectedMapId
    {
        get => _selectedMapId;
        set
        {
            if (!Set(ref _selectedMapId, value) || _syncingSelection) return;
            _syncingSelection = true;
            SelectedStation = value != null && _items.TryGetValue(value, out var item) && Stations.Contains(item) ? item : null;
            _syncingSelection = false;
            if (SelectedStation != null) SelectionScrollRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasSelection => _selected != null;

    /// <summary>Who a message about the selected entry goes to: the station, or an object's owner.</summary>
    public string? MessageTarget => _selected?.Station is { } s ? (s.IsObject ? s.ObjectOwner : s.Callsign) : null;
    public string SendMessageText => MessageTarget is { } t ? $"Message {t}…" : "Send message…";

    private IReadOnlyList<DetailRow> _details = [];
    public IReadOnlyList<DetailRow> Details { get => _details; private set => Set(ref _details, value); }

    private string _lastPacketText = "";
    public string LastPacketText { get => _lastPacketText; private set => Set(ref _lastPacketText, value); }

    private TrackedStation? _detailFor;
    private DateTimeOffset _detailTime;

    private void BuildDetail()
    {
        if (_selected?.Station is not { } s)
        {
            Details = [];
            LastPacketText = "";
            _detailFor = null;
            return;
        }
        var now = DateTimeOffset.Now;
        if (ReferenceEquals(s, _detailFor) && now - _detailTime < TimeSpan.FromSeconds(10)) return;
        _detailFor = s;
        _detailTime = now;
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<DetailRow>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add(new DetailRow(label, value)); }
        Add(s.IsObject ? "Object / item" : "Callsign", s.Callsign);
        Add("Sent by", s.IsObject ? s.ObjectOwner : null);
        Add("Last heard", $"{s.LastHeard.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({Converters.AgeConverter.Format(now - s.LastHeard)} ago)"
            + (now - s.LastHeard > StationFilter.StaleAfter ? " — stale" : ""));
        Add("First heard", s.FirstHeard.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
        Add("Packets", s.PacketCount.ToString("N0", CultureInfo.CurrentCulture));
        Add("Channel", s.Channel?.ToString(inv) ?? "none (not heard on a radio channel)");
        Add("Origin", s.Origin is PacketOrigin o ? PacketOriginText.Long(PacketDirection.Received, o) : null);
        Add("Last path", string.IsNullOrEmpty(s.Path) ? "(direct, no digipeaters)" : s.Path);
        Add("Audio level", s.AudioLevel?.ToString(inv));
        Add("Symbol", s.Symbol);
        if (s.HasPosition)
        {
            Add("Latitude", s.Latitude!.Value.ToString("0.00000", inv));
            Add("Longitude", s.Longitude!.Value.ToString("0.00000", inv));
            Add("Position from", s.PositionSource == PositionSources.DireWolfLog ? "Dire Wolf's CSV log" : "this application's parse of a received packet");
            Add("Position time", s.PositionTime is { } pt
                ? $"{pt.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({Converters.AgeConverter.Format(now - pt)} ago){(StationFilter.IsPositionStale(s, now) ? " — stale" : "")}"
                : "unknown");
            Add("Distance", StationFilter.DistanceText(s, HomeLatitude, HomeLongitude) is { Length: > 0 } d ? d + " from home" : HasHome ? null : "set your home position in Settings to see distance");
            Add("Altitude", s.AltitudeFeet is double alt ? $"{alt:0} ft ({alt * 0.3048:0} m)" : null);
            Add("Speed", s.SpeedKnots is double sp ? $"{sp:0.#} kn ({sp * 1.852:0.#} km/h)" : null);
            Add("Course", s.CourseDegrees is int c ? c + "°" : null);
        }
        else Add("Position", "none received (station not placed on the map)");
        Add("Track points", s.Track.Count.ToString(inv));
        Add("Status", s.Status);
        Add("Comment", s.Comment is { } cm ? PacketOriginText.OneLine(cm, 500) : null);
        Details = rows;

        // Last raw packet from this station (or the last one carrying this object), from the station's packet buffer.
        PacketRecord? last = null;
        var recent = _shell.Session.Packets.GetLatest(3000);
        for (int i = recent.Count - 1; i >= 0; i--)
        {
            var p = recent[i];
            if (p.Direction != PacketDirection.Received) continue;
            bool match = s.IsObject
                ? p.Aprs?.ObjectName is { } n && string.Equals(n.Trim(), s.Callsign.Trim(), StringComparison.OrdinalIgnoreCase)
                : string.Equals(p.Source, s.Callsign, StringComparison.OrdinalIgnoreCase);
            if (match) { last = p; break; }
        }
        LastPacketText = last != null
            ? $"{last.Time.ToLocalTime():HH:mm:ss} [{last.Label}] {last.RawText}"
            : "Not in the recent packet buffer (it may have come from Dire Wolf's CSV log, or been heard a while ago).";
    }

    private void CenterOnSelected()
    {
        if (_selected == null) return;
        var m = MapMarkers.FirstOrDefault(x => x.Id == _selected.Id);
        if (m != null) CenterRequested?.Invoke(this, m);
    }

    private void SendMessage()
    {
        if (MessageTarget is not { } to) return;
        MessagesViewModel.RequestCompose(to);
        _shell.Navigate<MessagesViewModel>();
    }

    // ---- Exports ----

    private async Task ExportStationsAsync()
    {
        var list = _filtered.ToList();
        var path = Dialogs.SaveFile("CSV file (*.csv)|*.csv", $"stations-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path == null) return;
        await Task.Run(() =>
        {
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            Exporters.WriteStationsCsv(list, w);
        });
        _shell.SetStatus($"Exported {list.Count:N0} station(s) to {path}");
    }

    private async Task ExportGpxAsync(IReadOnlyList<TrackedStation> stations, string what)
    {
        var list = stations.Where(s => s.Track.Count > 0).ToList();
        if (list.Count == 0)
        {
            Dialogs.Info("There are no real track points to export.", "Only positions actually received are exported; nothing is interpolated.");
            return;
        }
        var path = Dialogs.SaveFile("GPX track (*.gpx)|*.gpx", $"tracks-{DateTime.Now:yyyyMMdd-HHmmss}.gpx");
        if (path == null) return;
        await Task.Run(() =>
        {
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            Exporters.WriteGpx(list, w);
        });
        _shell.SetStatus($"Exported {list.Sum(s => s.Track.Count):N0} track point(s) of {what} to {path}");
    }
}
