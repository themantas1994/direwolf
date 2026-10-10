using System.Globalization;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Packets;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>One APRS message (incoming or outgoing) in a conversation.</summary>
public sealed class MessageItemViewModel : ObservableObject
{
    public MessageItemViewModel(AprsMessage m) => Update(m);

    private AprsMessage _message = null!;
    public AprsMessage Message { get => _message; private set => Set(ref _message, value); }

    public long LocalId => Message.LocalId;
    public bool IsOutgoing => Message.Direction == AprsMessageDirection.Outgoing;
    public string DirectionText => IsOutgoing ? $"To {Message.To}" : $"From {Message.From}";
    public string Text => Message.Text;
    public string TimeText => Message.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string IdText => Message.MessageId is { } id ? "#" + id : "no message number";
    public string StateText => MessageStateText.Describe(Message);
    public string StateKind => MessageStateText.Kind(Message);
    public string? ErrorText => Message.Error;
    public bool CanCancel => Message.State is AprsMessageState.Queued or AprsMessageState.Sent;
    /// <summary>Alignment hint for the view: outgoing on the right.</summary>
    public System.Windows.HorizontalAlignment Alignment => IsOutgoing ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;

    public void Update(AprsMessage m)
    {
        if (ReferenceEquals(_message, m)) return;
        Message = m;
        foreach (var n in new[] { nameof(StateText), nameof(StateKind), nameof(ErrorText), nameof(CanCancel), nameof(Text), nameof(DirectionText), nameof(IdText) })
            OnPropertyChanged(n);
    }
}

/// <summary>Plain-words message state (pure; testable).</summary>
public static class MessageStateText
{
    public static string Describe(AprsMessage m)
    {
        string T(DateTimeOffset? t) => t?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "?";
        return m.State switch
        {
            AprsMessageState.Queued => "Queued — about to transmit",
            AprsMessageState.Sent => $"Sent ({m.Tries} {(m.Tries == 1 ? "try" : "tries")}, last {T(m.LastSent)})"
                                     + (m.NextAttempt is { } n ? $" · waiting for ack, next retry {T(n)}" : " · waiting for ack"),
            AprsMessageState.Acknowledged => $"Acknowledged {T(m.Completed)} after {m.Tries} {(m.Tries == 1 ? "try" : "tries")}",
            AprsMessageState.Rejected => $"Rejected by the recipient {T(m.Completed)}",
            AprsMessageState.GaveUp => m.Error != null ? $"Not sent — {m.Error}" : $"Gave up after {m.Tries} tries without an ack ({T(m.Completed)})",
            AprsMessageState.Cancelled => $"Cancelled {T(m.Completed)} after {m.Tries} {(m.Tries == 1 ? "try" : "tries")}",
            AprsMessageState.Received => m.MessageId == null ? "Received (no message number, no ack expected)"
                                         : m.AckSent ? "Received · ack sent" : "Received · not acknowledged",
            _ => m.State.ToString(),
        };
    }

    /// <summary>Theme brush prefix for the state chip.</summary>
    public static string Kind(AprsMessage m) => m.State switch
    {
        AprsMessageState.Acknowledged => "Success",
        AprsMessageState.Rejected or AprsMessageState.GaveUp => "Error",
        AprsMessageState.Cancelled => "Stale",
        AprsMessageState.Queued or AprsMessageState.Sent => "Warning",
        AprsMessageState.Received => "Rx",
        _ => "Neutral",
    };

    public static string ShortState(AprsMessage m) => m.State switch
    {
        AprsMessageState.Sent => $"Sent ({m.Tries})",
        AprsMessageState.GaveUp => "Gave up",
        _ => m.State.ToString(),
    };

    /// <summary>Digipeater path from text such as "WIDE1-1, WIDE2-1"; null + error when invalid.</summary>
    public static IReadOnlyList<string>? ParsePath(string? text, out string? error)
    {
        error = null;
        var parts = (text ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToUpperInvariant()).ToList();
        if (parts.Count > 8) { error = "At most 8 digipeaters."; return null; }
        foreach (var p in parts)
        {
            if (!Ax25Address.TryParse(p, out _)) { error = $"\"{p}\" is not a valid digipeater (callsign, optional -SSID 0-15)."; return null; }
        }
        return parts;
    }
}

/// <summary>A conversation: all messages with one other station.</summary>
public sealed class ConversationItem : ObservableObject
{
    public ConversationItem(string callsign) => Callsign = callsign;

    public string Callsign { get; }

    private string _lastText = "";
    public string LastText { get => _lastText; private set => Set(ref _lastText, value); }

    private string _lastTime = "";
    public string LastTime { get => _lastTime; private set => Set(ref _lastTime, value); }

    private DateTimeOffset _lastActivity;
    public DateTimeOffset LastActivity { get => _lastActivity; private set => Set(ref _lastActivity, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private int _pending;
    public int PendingCount { get => _pending; private set => Set(ref _pending, value); }

    public void Update(IReadOnlyList<AprsMessage> messages)
    {
        var last = messages[^1];
        LastActivity = messages.Max(m => m.LastSent ?? m.Created);
        LastText = (last.Direction == AprsMessageDirection.Outgoing ? "You: " : "") + last.Text;
        LastTime = last.Created.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        PendingCount = messages.Count(m => m.State is AprsMessageState.Queued or AprsMessageState.Sent);
        int inc = messages.Count(m => m.Direction == AprsMessageDirection.Incoming);
        Summary = $"{messages.Count} message(s), {inc} received" + (PendingCount > 0 ? $", {PendingCount} awaiting ack" : "");
    }
}
