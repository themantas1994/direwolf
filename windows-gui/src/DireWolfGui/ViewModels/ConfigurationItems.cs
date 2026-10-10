using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using DireWolfGui.Core.Config;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>Small helpers shared by the configuration editors.</summary>
public static class ConfigEdit
{
    /// <summary>Context argument for FindDirective/SetDirective: only channel or audio device scoped directives take one.</summary>
    public static int? ScopeContext(string keyword, int? context) =>
        DirectiveCatalog.Find(keyword)?.Scope is DirectiveScope.Channel or DirectiveScope.AudioDevice ? context : null;

    /// <summary>Arguments as they would be typed on the line (quoted when needed).</summary>
    public static string JoinArgs(IEnumerable<string> args) => string.Join(" ", args.Select(ConfigTokenizer.Quote));

    public static string? WholeNumber(string v, int min, int max, string? what = null)
    {
        if (!ConfigFacts.TryInt(v.Trim(), out int n) || n < min || n > max)
            return $"{what ?? "The value"} must be a whole number from {min} to {max}.";
        return null;
    }

    public static string? Callsign(string v)
    {
        string c = v.Trim().ToUpperInvariant();
        if (!ConfigValidator.IsValidCall(c)) return "A callsign is 1-6 letters/digits with an optional -SSID from 0 to 15, e.g. N0CALL-10.";
        if (ConfigFacts.IsPlaceholderCall(c)) return "N0CALL / NOCALL is a placeholder. Use your own callsign.";
        return null;
    }

    public static IReadOnlyList<string> ComPorts { get; } = Enumerable.Range(1, 20).Select(i => "COM" + i.ToString(CultureInfo.InvariantCulture)).ToList();

    public static string SeverityKind(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "Error",
        DiagnosticSeverity.Warning => "Warning",
        _ => "Info",
    };
}

/// <summary>One directive in the guided editor: current value, help from the catalog, Apply and Disable.</summary>
public sealed class GuidedField : ObservableObject
{
    private readonly ConfigurationSession _session;
    private readonly Func<string, string?>? _validate;
    private readonly Func<string, bool>? _confirm;
    private readonly Func<string, string>? _normalize;
    private readonly int? _context;

    public GuidedField(ConfigurationSession session, ConfigDocument doc, string keyword, int? context, string label,
        string? explanation = null, string? defaultText = null, IEnumerable<string>? choices = null,
        Func<string, string?>? validate = null, Func<string, bool>? confirm = null, Func<string, string>? normalize = null)
    {
        _session = session;
        _validate = validate;
        _confirm = confirm;
        _normalize = normalize;
        Keyword = keyword;
        _context = ConfigEdit.ScopeContext(keyword, context);
        Label = label;
        Explanation = explanation;
        DefaultText = defaultText == null ? null : "Dire Wolf's default: " + defaultText;
        Info = DirectiveCatalog.Find(keyword);
        Choices = choices?.ToList() ?? [];
        var line = doc.FindDirective(keyword, _context);
        CurrentLine = line?.LineNumber;
        CurrentText = line == null ? "" : ConfigEdit.JoinArgs(line.Arguments);
        _value = CurrentText;
        ApplyCommand = new RelayCommand(Apply, () => IsChanged);
        DisableCommand = new RelayCommand(Disable, () => IsSet);
    }

    public string Key => Keyword + "|" + _context;
    public string Keyword { get; }
    public string Label { get; }
    public string? Explanation { get; }
    public string? DefaultText { get; }
    public DirectiveInfo? Info { get; }
    public string Help => Info?.Summary ?? "";
    public string Syntax => Info == null ? "" : "Syntax: " + Info.Syntax;
    public IReadOnlyList<string> Choices { get; }
    public bool HasChoices => Choices.Count > 0;
    public bool HasNoChoices => Choices.Count == 0;
    public int? CurrentLine { get; }
    public string CurrentText { get; }
    public bool IsSet => CurrentLine != null;
    public string StateText => IsSet ? $"Line {CurrentLine}" : "Not set (default applies)";

    private string _value;
    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value ?? "")) return;
            Error = string.IsNullOrWhiteSpace(_value) ? null : _validate?.Invoke(_value.Trim());
            OnPropertyChanged(nameof(IsChanged));
        }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool IsChanged => !string.Equals(Value.Trim(), CurrentText, StringComparison.Ordinal);

    public RelayCommand ApplyCommand { get; }
    public RelayCommand DisableCommand { get; }

    private void Apply()
    {
        string v = Value.Trim();
        if (v.Length == 0)
        {
            if (IsSet) Disable();
            return;
        }
        if (_validate?.Invoke(v) is { } err)
        {
            Error = err;
            Dialogs.Warning($"{Label}: {err}");
            return;
        }
        if (_normalize != null) v = _normalize(v);
        if (_confirm != null && !_confirm(v)) return;
        _session.Edit(d => d.SetDirective(Keyword, [v], _context), $"{Keyword} set to {v}.");
    }

    private void Disable()
    {
        if (!Dialogs.Confirm($"Disable the {Keyword} line{(CurrentLine is int l ? $" (line {l})" : "")}?\n\nIt is commented out with a \"disabled by Dire Wolf Station\" marker, not deleted, and can be enabled again on the All directives tab. Dire Wolf then uses its default."))
            return;
        _session.Edit(d => d.DisableDirective(Keyword, _context, "disabled in the Configuration page"), $"{Keyword} disabled.");
    }
}

/// <summary>ADEVICEn input/output editor with the Windows (WinMM) device names.</summary>
public sealed class AudioDeviceEditor : ObservableObject
{
    private readonly ConfigurationSession _session;

    public AudioDeviceEditor(ConfigurationSession session, ConfigDocument doc, int device,
        IReadOnlyList<AudioDevice> inputs, IReadOnlyList<AudioDevice> outputs)
    {
        _session = session;
        Device = device;
        var line = doc.FindDirective("ADEVICE", device);
        CurrentLine = line?.LineNumber;
        var args = line?.Arguments ?? [];
        CurrentInput = args.Count > 0 ? ConfigTokenizer.Quote(args[0]) : "";
        CurrentOutput = args.Count > 1 ? ConfigTokenizer.Quote(args[1]) : "";
        _input = CurrentInput;
        _output = CurrentOutput;
        InputChoices = inputs.Select(d => WinMmAudio.AdeviceToken(d, inputs)).Distinct().ToList();
        OutputChoices = outputs.Select(d => WinMmAudio.AdeviceToken(d, outputs)).Distinct().ToList();
        InputList = inputs.Count == 0 ? "No input devices found (or not running on Windows)." : string.Join("\n", inputs.Select(d => $"{d.Number}: {d.Name} ({d.Channels} ch)"));
        OutputList = outputs.Count == 0 ? "No output devices found (or not running on Windows)." : string.Join("\n", outputs.Select(d => $"{d.Number}: {d.Name} ({d.Channels} ch)"));
        ApplyCommand = new RelayCommand(Apply, () => IsChanged && Input.Trim().Length > 0);
    }

    public int Device { get; }
    public string Keyword => Device == 0 ? "ADEVICE" : $"ADEVICE{Device}";
    public string Title => $"Audio device {Device} ({Keyword}): radio channel {Device * 2}, and {Device * 2 + 1} in stereo";
    public int? CurrentLine { get; }
    public string StateText => CurrentLine is int l ? $"Line {l}" : "Not set" + (Device == 0 ? " (Dire Wolf uses the Windows default devices)" : "");
    public string CurrentInput { get; }
    public string CurrentOutput { get; }
    public IReadOnlyList<string> InputChoices { get; }
    public IReadOnlyList<string> OutputChoices { get; }
    public string InputList { get; }
    public string OutputList { get; }
    public string Help => DirectiveCatalog.Find("ADEVICE")?.Summary +
        " Use the device number or a part of its name (as Windows shows it, at most 31 characters, case-sensitive); names survive devices being plugged in or out, numbers can shift. Quote names with spaces.";

    private string _input;
    public string Input
    {
        get => _input;
        set { if (Set(ref _input, value ?? "")) OnPropertyChanged(nameof(IsChanged)); }
    }

    private string _output;
    public string Output
    {
        get => _output;
        set { if (Set(ref _output, value ?? "")) OnPropertyChanged(nameof(IsChanged)); }
    }

    public bool IsChanged => Input.Trim() != CurrentInput || Output.Trim() != CurrentOutput;
    public RelayCommand ApplyCommand { get; }

    private void Apply()
    {
        string i = Input.Trim(), o = Output.Trim();
        if (i.Contains('\n') || o.Contains('\n')) return;
        var args = o.Length == 0 ? new[] { i } : [i, o];
        _session.Edit(d => d.SetDirective(Keyword, args, Device), $"{Keyword} set.");
    }
}

/// <summary>PTT method editor for one channel (serial RTS/DTR, CM108, or the line as typed).</summary>
public sealed class PttEditor : ObservableObject
{
    private readonly ConfigurationSession _session;

    public static IReadOnlyList<string> Methods { get; } =
    [
        "No PTT line (receive only, or VOX)",
        "Serial port RTS line",
        "Serial port DTR line",
        "CM108 / CM119 USB audio GPIO",
        "Other (type the PTT arguments)",
    ];

    public PttEditor(ConfigurationSession session, ConfigDocument doc, int channel)
    {
        _session = session;
        Channel = channel;
        var line = doc.FindDirective("PTT", channel);
        CurrentLine = line?.LineNumber;
        var a = line?.Arguments ?? [];
        _currentText = ConfigEdit.JoinArgs(a);
        _other = _currentText;
        if (line == null) _method = 0;
        else if (a.Count == 2 && Regex.IsMatch(a[1], "^-?(RTS|DTR)$", RegexOptions.IgnoreCase))
        {
            _method = a[1].TrimStart('-').Equals("RTS", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            _port = a[0];
            _invert = a[1].StartsWith('-');
        }
        else if (a.Count >= 1 && a[0].Equals("CM108", StringComparison.OrdinalIgnoreCase))
        {
            _method = 3;
            int i = 1;
            if (a.Count > 1 && Regex.IsMatch(a[1], @"^-?\d+$"))
            {
                _invert = a[1].StartsWith('-');
                _gpio = a[1].TrimStart('-');
                i = 2;
            }
            if (a.Count > i) _hidPath = a[i];
        }
        else _method = 4;
        _savedMethod = _method;
        ApplyCommand = new RelayCommand(Apply, () => IsChanged);
    }

    private readonly string _currentText;
    private readonly int _savedMethod;

    public int Channel { get; }
    public int? CurrentLine { get; }
    public string StateText => CurrentLine is int l ? $"Line {l}: PTT {_currentText}" : "No PTT line: this channel cannot key a transmitter (unless the radio uses VOX).";
    public IReadOnlyList<string> MethodList => Methods;
    public IReadOnlyList<string> Ports => ConfigEdit.ComPorts;
    public string Help => DirectiveCatalog.Find("PTT")?.Summary + " Windows builds support serial RTS/DTR and (since 1.7) CM108 GPIO.";

    private int _method;
    public int Method
    {
        get => _method;
        set { if (Set(ref _method, value)) Changed(); }
    }

    private string _port = "COM1";
    public string Port { get => _port; set { if (Set(ref _port, value ?? "")) Changed(); } }

    private bool _invert;
    public bool Invert { get => _invert; set { if (Set(ref _invert, value)) Changed(); } }

    private string _gpio = "";
    public string Gpio { get => _gpio; set { if (Set(ref _gpio, value ?? "")) Changed(); } }

    private string _hidPath = "";
    public string HidPath { get => _hidPath; set { if (Set(ref _hidPath, value ?? "")) Changed(); } }

    private string _other;
    public string Other { get => _other; set { if (Set(ref _other, value ?? "")) Changed(); } }

    public bool IsSerial => Method is 1 or 2;
    public bool IsCm108 => Method == 3;
    public bool IsOther => Method == 4;

    private void Changed()
    {
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsCm108));
        OnPropertyChanged(nameof(IsOther));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(Preview));
    }

    private string[] Args()
    {
        switch (Method)
        {
            case 1 or 2:
                return [Port.Trim().ToUpperInvariant(), (Invert ? "-" : "") + (Method == 1 ? "RTS" : "DTR")];
            case 3:
            {
                var l = new List<string> { "CM108" };
                if (Gpio.Trim().Length > 0 || Invert) l.Add((Invert ? "-" : "") + (Gpio.Trim().Length > 0 ? Gpio.Trim() : "3"));
                if (HidPath.Trim().Length > 0) l.Add(ConfigTokenizer.Quote(HidPath.Trim()));
                return l.ToArray();
            }
            case 4:
                return [Other.Trim()];
            default:
                return [];
        }
    }

    public string Preview => Method == 0 ? "(no PTT line)" : "PTT " + string.Join(" ", Args());
    public bool IsChanged => Method == 0 ? _savedMethod != 0 : !string.Equals(string.Join(" ", Args()), _currentText, StringComparison.Ordinal);

    public RelayCommand ApplyCommand { get; }

    private void Apply()
    {
        if (Method == 0)
        {
            if (CurrentLine == null) return;
            _session.Edit(d => d.DisableDirective("PTT", Channel, "no PTT selected in the Configuration page"), $"PTT for channel {Channel} disabled.");
            return;
        }
        if (IsSerial && !Regex.IsMatch(Port.Trim(), @"^(COM\d+|/dev/\S+)$", RegexOptions.IgnoreCase))
        {
            Dialogs.Warning("Enter a serial port such as COM3.");
            return;
        }
        if (IsCm108 && Gpio.Trim().Length > 0 && !Regex.IsMatch(Gpio.Trim(), @"^\d+$"))
        {
            Dialogs.Warning("The CM108 GPIO number is a whole number (usually 3).");
            return;
        }
        if (IsOther && Other.Trim().Length == 0)
        {
            Dialogs.Warning("Type the PTT arguments, e.g. COM3 RTS.");
            return;
        }
        if (!OperatorNotice.ConfirmTransmit($"Set PTT for channel {Channel}: {Preview}",
                "With a PTT method Dire Wolf can key your transmitter whenever something on this channel transmits: " +
                "beacons, digipeating, the IGate, or any client application connected to the AGW/KISS ports."))
            return;
        var args = Args();
        _session.Edit(d => d.SetDirective("PTT", args, Channel), $"PTT for channel {Channel} set.");
    }
}

/// <summary>A radio channel in the guided editor.</summary>
public sealed class GuidedChannel
{
    public GuidedChannel(int number, string title, string? note, bool isWapr, IEnumerable<GuidedField> fields, PttEditor ptt)
    {
        Number = number;
        Title = title;
        Note = note;
        IsWapr = isWapr;
        Fields = fields.ToList();
        Ptt = ptt;
    }

    public int Number { get; }
    public string Title { get; }
    public string? Note { get; }
    public bool IsWapr { get; }
    public IReadOnlyList<GuidedField> Fields { get; }
    public PttEditor Ptt { get; }
}

/// <summary>An audio device in the guided editor.</summary>
public sealed class GuidedDevice(AudioDeviceEditor editor, IEnumerable<GuidedField> fields)
{
    public AudioDeviceEditor Editor { get; } = editor;
    public IReadOnlyList<GuidedField> Fields { get; } = fields.ToList();
}

/// <summary>A KISSPORT line.</summary>
public sealed class KissPortRow : ObservableObject
{
    public KissPortRow(ConfigLine line)
    {
        Index = line.Index;
        LineNumber = line.LineNumber;
        CurrentPort = line.Arguments.Count > 0 ? line.Arguments[0] : "";
        CurrentChannel = line.Arguments.Count > 1 ? line.Arguments[1] : "";
        _port = CurrentPort;
        _channel = CurrentChannel;
        TrailingComment = line.TrailingComment;
        Indent = line.Text[..(line.Text.Length - line.Text.TrimStart(' ', '\t').Length)];
    }

    public int Index { get; }
    public int LineNumber { get; }
    public string CurrentPort { get; }
    public string CurrentChannel { get; }
    public string? TrailingComment { get; }
    public string Indent { get; }
    public string Description => CurrentPort == "0" ? "KISSPORT 0: the default KISS port 8001 is not opened."
        : $"KISS over TCP on port {CurrentPort}, {(CurrentChannel.Length == 0 ? "all radio channels" : "channel " + CurrentChannel)}";

    private string _port;
    public string Port { get => _port; set { if (Set(ref _port, value ?? "")) OnPropertyChanged(nameof(IsChanged)); } }

    private string _channel;
    public string Channel { get => _channel; set { if (Set(ref _channel, value ?? "")) OnPropertyChanged(nameof(IsChanged)); } }

    public bool IsChanged => Port.Trim() != CurrentPort || Channel.Trim() != CurrentChannel;
}

/// <summary>A directive line (or a line disabled by this program) in the All directives list.</summary>
public sealed class DirectiveRow
{
    public required int Index { get; init; }
    public required int LineNumber { get; init; }
    public required string Keyword { get; init; }
    public required string Text { get; init; }
    public string ContextText { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Category { get; init; } = "";
    public string Diagnostics { get; init; } = "";
    public string SeverityKind { get; init; } = "";
    public bool HasDiagnostics => Diagnostics.Length > 0;
    public bool IsDisabled { get; init; }
    public bool IsEnabled => !IsDisabled;
    public bool IsUnknown { get; init; }
    public bool Transmits { get; init; }
    public bool Forwards { get; init; }
    public string Flags => string.Join(" ", new[] { IsDisabled ? "disabled" : null, Transmits ? "can transmit" : null, Forwards ? "forwards" : null, IsUnknown ? "unknown" : null }.Where(s => s != null));
    public string SearchText => $"{Keyword} {Text} {Summary} {Category} {Diagnostics} {ContextText}";
}

/// <summary>A diagnostic for display.</summary>
public sealed class DiagnosticRow(ConfigDiagnostic d)
{
    public ConfigDiagnostic Diagnostic { get; } = d;

    public int? Line => Diagnostic.Line;
    public string LineText => Diagnostic.Line is int l ? $"Line {l}" : "—";
    public string Severity => Diagnostic.Severity.ToString();
    public string Kind => ConfigEdit.SeverityKind(Diagnostic.Severity);
    public string Code => Diagnostic.Code;
    public string Message => Diagnostic.Message;
    public string Source => Diagnostic.Source == DiagnosticSource.DireWolf ? "Dire Wolf" : "Dire Wolf Station";
}

public sealed class BackupRow(ConfigBackup b)
{
    public ConfigBackup Backup { get; } = b;
    public string When => Backup.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string Size => Backup.Size < 1024 ? $"{Backup.Size} bytes" : $"{Backup.Size / 1024.0:0.0} KB";
    public string FileName => System.IO.Path.GetFileName(Backup.Path);
}

public sealed class ProfileRow(ConfigProfile p, bool isCurrent)
{
    public ConfigProfile Profile { get; } = p;
    public string Name => Profile.Name;
    public string Modified => Profile.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string Size => Profile.Size < 1024 ? $"{Profile.Size} bytes" : $"{Profile.Size / 1024.0:0.0} KB";
    public bool IsCurrent { get; } = isCurrent;
    public string Path => Profile.Path;
}

/// <summary>Observable list replaced as a whole (cheap for the small lists on these pages).</summary>
public static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> c, IEnumerable<T> items)
    {
        c.Clear();
        foreach (var i in items) c.Add(i);
    }
}
