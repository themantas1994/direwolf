using System.Collections.ObjectModel;
using System.Windows.Input;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Process;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>"APRS messages": conversations, message states and a composer that transmits only on Send.</summary>
public sealed class MessagesViewModel : PageViewModel
{
    public const string DefaultPath = "WIDE1-1,WIDE2-1";

    private static string? s_pendingComposeTo;
    private readonly IShell _shell;
    private readonly Dictionary<string, ConversationItem> _conversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, MessageItemViewModel> _messageItems = new();
    private long _lastVersion = -1;
    private bool _confirmedThisSession;
    private IReadOnlyList<AprsMessage> _all = [];

    public MessagesViewModel(IShell shell) : base("APRS messages", "", "Ctrl+4")
    {
        _shell = shell;
        Channels.Add(0);
        SendCommand = new AsyncCommand(SendAsync, () => CanSend);
        CancelCommand = new RelayCommand(p => Cancel(p as MessageItemViewModel), p => p is MessageItemViewModel { CanCancel: true });
        ReplyCommand = new RelayCommand(p => { if (p is ConversationItem c) ComposeTo = c.Callsign; });
        NewConversationCommand = new RelayCommand(() => { SelectedConversation = null; ComposeTo = ""; ComposeText = ""; });
        ResetPathCommand = new RelayCommand(() => ComposePath = DefaultPath);
    }

    /// <summary>Another page (e.g. the station list) asks for a message to <paramref name="callsign"/>; applied when this page is shown.</summary>
    public static void RequestCompose(string callsign) => s_pendingComposeTo = callsign;

    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();
    public ObservableCollection<int> Channels { get; } = new();

    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReplyCommand { get; }
    public ICommand NewConversationCommand { get; }
    public ICommand ResetPathCommand { get; }

    public int MaxTextLength => AprsMessageService.MaxTextLength;

    // ---- Compose ----

    private string _composeTo = "";
    public string ComposeTo
    {
        get => _composeTo;
        set { if (Set(ref _composeTo, (value ?? "").Trim().ToUpperInvariant())) UpdateCanSend(); }
    }

    private string _composeText = "";
    public string ComposeText
    {
        get => _composeText;
        set
        {
            if (!Set(ref _composeText, value ?? "")) return;
            OnPropertyChanged(nameof(CounterText));
            UpdateCanSend();
        }
    }

    public string CounterText => $"{ComposeText.Length}/{MaxTextLength}";

    private int _composeChannel;
    public int ComposeChannel { get => _composeChannel; set => Set(ref _composeChannel, value); }

    private string _composePath = DefaultPath;
    public string ComposePath { get => _composePath; set { if (Set(ref _composePath, value ?? "")) UpdateCanSend(); } }

    private bool _canSend;
    public bool CanSend { get => _canSend; private set => Set(ref _canSend, value); }

    private string _sendBlockedReason = "";
    /// <summary>Why Send is disabled (station not running, AGW not connected, messaging off, input invalid).</summary>
    public string SendBlockedReason { get => _sendBlockedReason; private set => Set(ref _sendBlockedReason, value); }

    private bool _messagingDisabled;
    /// <summary>Messaging is off in Settings: show the "Enable messaging in Settings" hint.</summary>
    public bool MessagingDisabled { get => _messagingDisabled; private set => Set(ref _messagingDisabled, value); }

    private string _myCallText = "";
    public string MyCallText { get => _myCallText; private set => Set(ref _myCallText, value); }

    private string _settingsText = "";
    public string SettingsText { get => _settingsText; private set => Set(ref _settingsText, value); }

    private void UpdateCanSend()
    {
        var session = _shell.Session;
        string reason;
        if (!_shell.Settings.MessagingEnabled)
            reason = "Messaging is turned off. Enable messaging in Settings (it transmits on the air).";
        else if (session.State != DireWolfState.Running)
            reason = "Dire Wolf is not running — press Start (F5).";
        else if (!session.AgwMonitorReady)
            reason = "Waiting for the connection to Dire Wolf's AGW port (messages are sent through it). Check that AGWPORT is enabled in the configuration.";
        else if (string.IsNullOrWhiteSpace(session.Messages.MyCall))
            reason = "Your callsign is not known. Set MYCALL in the Dire Wolf configuration.";
        else if (ComposeTo.Length is < 1 or > 9)
            reason = "Enter the recipient's callsign (1 to 9 characters).";
        else if (AprsMessageService.ValidateText(ComposeText) is { } err)
            reason = err == "The message is empty." ? "Type a message." : err;
        else if (MessageStateText.ParsePath(ComposePath, out var pathErr) == null)
            reason = "Path: " + pathErr;
        else reason = "";
        SendBlockedReason = reason;
        bool was = CanSend;
        CanSend = reason.Length == 0;
        if (was != CanSend) CommandManager.InvalidateRequerySuggested();
        MessagingDisabled = !_shell.Settings.MessagingEnabled;
    }

    /// <summary>Settings → message service. Cheap; done on show, before sending and in the background.</summary>
    private void ApplySettings()
    {
        var s = _shell.Settings;
        var svc = _shell.Session.Messages;
        svc.AutoAcknowledge = s.MessagingEnabled && s.AutoAck;
        svc.MaxTries = 1 + Math.Clamp(s.MessageRetryCount, 0, 20);   // first transmission + retries
        svc.FirstRetryDelay = TimeSpan.FromSeconds(Math.Clamp(s.MessageRetrySeconds, 5, 3600));
        MyCallText = string.IsNullOrWhiteSpace(svc.MyCall) ? "Your callsign: not known yet (from MYCALL when Dire Wolf starts)" : "Sending as " + svc.MyCall.ToUpperInvariant();
        SettingsText = (s.MessagingEnabled ? "Messaging on" : "Messaging off")
            + $" · auto-acknowledge {(svc.AutoAcknowledge ? "on" : "off")}"
            + $" · up to {svc.MaxTries} transmissions, first retry after {svc.FirstRetryDelay.TotalSeconds:0} s (then doubling)";
    }

    protected override void OnShown()
    {
        ApplySettings();
        foreach (var ch in _shell.Session.ChannelActivity.Select(a => a.Channel))
            if (!Channels.Contains(ch)) Channels.Add(ch);
        if (s_pendingComposeTo is { } to)
        {
            s_pendingComposeTo = null;
            ComposeTo = to;
            _lastVersion = -1;
            Refresh();
            SelectedConversation = _conversations.TryGetValue(AprsMessageService.Normalize(to), out var c) ? c : null;
            ComposeTo = to;
            _focusPending = true;
            FocusComposeRequested?.Invoke(this, EventArgs.Empty);
        }
        Tick();
    }

    /// <summary>The view moves keyboard focus to the message text box.</summary>
    public event EventHandler? FocusComposeRequested;

    private bool _focusPending;
    /// <summary>True once after another page asked to compose a message (the view focuses the text box when it loads).</summary>
    public bool TakeFocusRequest()
    {
        bool r = _focusPending;
        _focusPending = false;
        return r;
    }

    public override void BackgroundTick() => ApplySettingsQuietly();

    private void ApplySettingsQuietly()
    {
        // Auto-ack must follow the settings even while this page is not open.
        var s = _shell.Settings;
        _shell.Session.Messages.AutoAcknowledge = s.MessagingEnabled && s.AutoAck;
    }

    public override void Tick()
    {
        UpdateCanSend();
        if (_shell.Session.Messages.Version != _lastVersion) Refresh();
        EmptyText = _all.Count > 0 ? ""
            : _shell.Session.State != DireWolfState.Running ? "No messages. Dire Wolf is not running — press Start (F5)."
            : "No messages yet. Messages addressed to your callsign and the ones you send appear here.";
    }

    private string _emptyText = "";
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    private void Refresh()
    {
        var svc = _shell.Session.Messages;
        _lastVersion = svc.Version;
        _all = svc.GetMessages();
        var groups = _all.GroupBy(m => AprsMessageService.Normalize(m.Direction == AprsMessageDirection.Outgoing ? m.To : m.From), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, List: (IReadOnlyList<AprsMessage>)g.OrderBy(m => m.Created).ToList()))
            .ToList();
        var wanted = new List<ConversationItem>();
        foreach (var (key, list) in groups)
        {
            if (!_conversations.TryGetValue(key, out var c)) _conversations[key] = c = new ConversationItem(key);
            c.Update(list);
            wanted.Add(c);
        }
        wanted = wanted.OrderByDescending(c => c.LastActivity).ToList();
        var selected = SelectedConversation;
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Conversations.Count && ReferenceEquals(Conversations[i], wanted[i])) continue;
            int j = Conversations.IndexOf(wanted[i]);
            if (j > i) Conversations.Move(j, i); else Conversations.Insert(i, wanted[i]);
        }
        while (Conversations.Count > wanted.Count) Conversations.RemoveAt(Conversations.Count - 1);
        if (selected != null && !ReferenceEquals(SelectedConversation, selected) && Conversations.Contains(selected)) SelectedConversation = selected;
        RefreshMessages();
    }

    private void RefreshMessages()
    {
        var key = SelectedConversation?.Callsign;
        var shown = _all.Where(m => key == null
                || string.Equals(AprsMessageService.Normalize(m.Direction == AprsMessageDirection.Outgoing ? m.To : m.From), key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Created).ToList();
        var ids = new HashSet<long>(_all.Select(m => m.LocalId));
        foreach (var k in _messageItems.Keys.Where(k => !ids.Contains(k)).ToList()) _messageItems.Remove(k);
        var wanted = new List<MessageItemViewModel>(shown.Count);
        foreach (var m in shown)
        {
            if (_messageItems.TryGetValue(m.LocalId, out var item)) item.Update(m);
            else _messageItems[m.LocalId] = item = new MessageItemViewModel(m);
            wanted.Add(item);
        }
        bool same = wanted.Count == Messages.Count && wanted.Zip(Messages).All(p => ReferenceEquals(p.First, p.Second));
        if (!same)
        {
            // Usually only appends; otherwise rebuild (at most a few hundred items).
            bool prefix = Messages.Count <= wanted.Count && Messages.Select((m, i) => ReferenceEquals(m, wanted[i])).All(x => x);
            if (!prefix) Messages.Clear();
            for (int i = Messages.Count; i < wanted.Count; i++) Messages.Add(wanted[i]);
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
        ConversationTitle = key == null ? "All messages" : "Conversation with " + key;
    }

    /// <summary>The view scrolls the message list to the newest message.</summary>
    public event EventHandler? ScrollToEndRequested;

    private string _conversationTitle = "All messages";
    public string ConversationTitle { get => _conversationTitle; private set => Set(ref _conversationTitle, value); }

    private ConversationItem? _selectedConversation;
    public ConversationItem? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (!Set(ref _selectedConversation, value)) return;
            if (value != null) ComposeTo = value.Callsign;
            RefreshMessages();
        }
    }

    // ---- Actions ----

    private async Task SendAsync()
    {
        ApplySettings();
        UpdateCanSend();
        if (!CanSend) return;
        var svc = _shell.Session.Messages;
        var path = MessageStateText.ParsePath(ComposePath, out _) ?? [];
        string to = ComposeTo, text = ComposeText;
        if (!_confirmedThisSession)
        {
            var how = path.Count > 0 ? $"via {string.Join(",", path)}" : "without digipeater path";
            if (!Dialogs.Confirm(
                    $"This transmits an APRS message on the air from {svc.MyCall.ToUpperInvariant()} to {to} on channel {ComposeChannel} {how}.\n\n" +
                    $"This application retransmits it (up to {svc.MaxTries} transmissions) until {to} acknowledges it, or you cancel it.\n\n" +
                    "Send it? (You will not be asked again until the application restarts.)",
                    "Transmit APRS message", warning: true))
                return;
            _confirmedThisSession = true;
        }
        // Core gap: channel and path are service-wide, so retries and acks use the latest values set here.
        svc.Channel = ComposeChannel;
        svc.Path = path;
        var result = await svc.SendAsync(to, text);
        if (result.State == AprsMessageState.GaveUp && result.Error != null)
            Dialogs.Error("The message could not be sent.", result.Error);
        else
        {
            ComposeText = "";
            _shell.SetStatus($"Message {result.MessageId} to {to} transmitted; waiting for acknowledgement.");
        }
        _lastVersion = -1;
        Refresh();
        SelectedConversation = _conversations.TryGetValue(AprsMessageService.Normalize(to), out var c) ? c : SelectedConversation;
    }

    private void Cancel(MessageItemViewModel? item)
    {
        if (item == null) return;
        if (_shell.Session.Messages.Cancel(item.LocalId)) _shell.SetStatus($"Stopped retrying message {item.IdText} to {item.Message.To}.");
        Refresh();
    }
}
