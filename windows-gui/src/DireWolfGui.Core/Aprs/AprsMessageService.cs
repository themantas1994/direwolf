using System.Globalization;
using DireWolfGui.Core.Net;
using DireWolfGui.Core.Packets;

namespace DireWolfGui.Core.Aprs;

public enum AprsMessageState { Queued, Sent, Acknowledged, Rejected, GaveUp, Cancelled, Received }

public enum AprsMessageDirection { Outgoing, Incoming }

public sealed record AprsMessage
{
    public long LocalId { get; init; }
    public AprsMessageDirection Direction { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Text { get; init; }
    /// <summary>APRS message number ("{id}"); null for messages without one.</summary>
    public string? MessageId { get; init; }
    public AprsMessageState State { get; init; }
    public int Tries { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset? LastSent { get; init; }
    public DateTimeOffset? NextAttempt { get; init; }
    public DateTimeOffset? Completed { get; init; }
    public string? Error { get; init; }
    /// <summary>For incoming messages: whether we sent an ack.</summary>
    public bool AckSent { get; init; }
}

/// <summary>
/// APRS one-to-one messaging: compose with message number, retry until acknowledged, track ack/rej,
/// receive messages to my call(s). Transmits only from <see cref="SendAsync"/>, its retries, and (when
/// <see cref="AutoAcknowledge"/> is on) acks for messages addressed to us.
/// </summary>
public sealed class AprsMessageService : IDisposable
{
    public const int MaxTextLength = 67;

    private readonly IPacketSender _sender;
    private readonly TimeProvider _time;
    private readonly List<AprsMessage> _messages = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _processing = new(1, 1);
    private ITimer? _timer;
    private long _nextLocalId;
    private int _nextMsgNumber = 1;
    private long _version;

    public AprsMessageService(IPacketSender sender, TimeProvider? timeProvider = null)
    {
        _sender = sender;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Our callsign (source of sent messages).</summary>
    public string MyCall { get; set; } = "";
    /// <summary>Other calls (e.g. other SSIDs) whose messages we also accept.</summary>
    public IReadOnlyCollection<string> AdditionalCalls { get; set; } = Array.Empty<string>();
    /// <summary>AGW port / radio channel to transmit on.</summary>
    public int Channel { get; set; }
    public IReadOnlyList<string> Path { get; set; } = new[] { "WIDE1-1", "WIDE2-1" };
    /// <summary>Destination (tocall) for our packets.</summary>
    public string Destination { get; set; } = "APDW18";
    public int MaxTries { get; set; } = 3;
    /// <summary>Delay before the first retry; doubles for each following one.</summary>
    public TimeSpan FirstRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Send "ack" for received messages with a number. Off by default; the GUI enables it with messaging.</summary>
    public bool AutoAcknowledge { get; set; }
    public int MaxMessages { get; set; } = 1000;

    public long Version => Interlocked.Read(ref _version);
    public event Action<AprsMessage>? MessageChanged;

    public IReadOnlyList<AprsMessage> GetMessages() { lock (_lock) return _messages.ToArray(); }

    /// <summary>Starts the retry timer (1 s tick).</summary>
    public void Start() => _timer ??= _time.CreateTimer(_ => _ = ProcessDueAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public static string Normalize(string call)
    {
        call = call.Trim().ToUpperInvariant();
        return call.EndsWith("-0", StringComparison.Ordinal) ? call[..^2] : call;
    }

    public bool IsMine(string? addressee)
    {
        if (string.IsNullOrWhiteSpace(addressee)) return false;
        string a = Normalize(addressee);
        return (MyCall.Length > 0 && a == Normalize(MyCall)) || AdditionalCalls.Any(c => Normalize(c) == a);
    }

    /// <summary>Builds the info field ":ADDRESSEE:text{id}".</summary>
    public static string ComposeInfo(string addressee, string text, string? messageId) =>
        ":" + addressee.Trim().ToUpperInvariant().PadRight(9)[..9] + ":" + text + (messageId != null ? "{" + messageId : "");

    public static string? ValidateText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "The message is empty.";
        if (text.Length > MaxTextLength) return $"Messages are limited to {MaxTextLength} characters.";
        if (text.IndexOfAny(new[] { '{', '|', '~' }) >= 0) return "Messages cannot contain the characters { | ~";
        return null;
    }

    /// <summary>Sends a message now and schedules retries until it is acknowledged.</summary>
    public async Task<AprsMessage> SendAsync(string to, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(MyCall)) throw new InvalidOperationException("Set your callsign before sending messages.");
        to = to.Trim().ToUpperInvariant();
        if (to.Length is < 1 or > 9) throw new ArgumentException("The addressee must be 1 to 9 characters.", nameof(to));
        if (ValidateText(text) is string err) throw new ArgumentException(err, nameof(text));
        AprsMessage m;
        lock (_lock)
        {
            string id = _nextMsgNumber.ToString(CultureInfo.InvariantCulture);
            _nextMsgNumber = _nextMsgNumber >= 99999 ? 1 : _nextMsgNumber + 1;
            m = new AprsMessage
            {
                LocalId = ++_nextLocalId, Direction = AprsMessageDirection.Outgoing, From = MyCall.ToUpperInvariant(), To = to, Text = text,
                MessageId = id, State = AprsMessageState.Queued, Created = _time.GetUtcNow(),
            };
            Add(m);
        }
        return await TransmitAsync(m, ct).ConfigureAwait(false);
    }

    private async Task<AprsMessage> TransmitAsync(AprsMessage m, CancellationToken ct)
    {
        try
        {
            await _sender.SendUnprotoAsync(Channel, m.From, Destination, Path, ComposeInfo(m.To, m.Text, m.MessageId), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Replace(m.LocalId, x => x.State is AprsMessageState.Queued or AprsMessageState.Sent
                ? x with { State = AprsMessageState.GaveUp, Error = "Could not send: " + ex.Message, NextAttempt = null, Completed = _time.GetUtcNow() }
                : x) ?? m;
        }
        var now = _time.GetUtcNow();
        return Replace(m.LocalId, x => x.State is AprsMessageState.Queued or AprsMessageState.Sent
            ? x with
            {
                State = AprsMessageState.Sent, Tries = x.Tries + 1, LastSent = now,
                NextAttempt = now + FirstRetryDelay * Math.Pow(2, x.Tries),
            }
            : x) ?? m;
    }

    /// <summary>Retries or gives up on messages whose time has come. Called by the timer; tests may call it directly.</summary>
    public async Task ProcessDueAsync(CancellationToken ct = default)
    {
        if (!await _processing.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            var now = _time.GetUtcNow();
            List<AprsMessage> due;
            lock (_lock) due = _messages.Where(m => m.State == AprsMessageState.Sent && m.NextAttempt <= now).ToList();
            foreach (var m in due)
            {
                if (m.Tries >= MaxTries)
                    Replace(m.LocalId, x => x.State == AprsMessageState.Sent ? x with { State = AprsMessageState.GaveUp, NextAttempt = null, Completed = now } : x);
                else
                    await TransmitAsync(m, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _processing.Release();
        }
    }

    public bool Cancel(long localId) =>
        Replace(localId, x => x.State is AprsMessageState.Queued or AprsMessageState.Sent
            ? x with { State = AprsMessageState.Cancelled, NextAttempt = null, Completed = _time.GetUtcNow() } : x)?.State == AprsMessageState.Cancelled;

    /// <summary>
    /// Looks at a received packet: acks/rejects for our messages, and messages addressed to us.
    /// Returns the new or updated message, if any.
    /// </summary>
    public AprsMessage? HandleIncoming(PacketRecord p)
    {
        if (p.Direction != PacketDirection.Received) return null;
        var a = p.Aprs ?? AprsParser.Parse(p.Destination, p.Info);
        if (a.Type is not (AprsPacketType.Message or AprsPacketType.MessageAck or AprsPacketType.MessageReject)) return null;
        if (!IsMine(a.Addressee)) return null;
        string from = Normalize(p.Source);

        if (a.Type is AprsPacketType.MessageAck or AprsPacketType.MessageReject)
            return Resolve(from, a.MessageId!, a.Type == AprsPacketType.MessageAck ? AprsMessageState.Acknowledged : AprsMessageState.Rejected);

        if (a.ReplyAck != null) Resolve(from, a.ReplyAck, AprsMessageState.Acknowledged);

        var now = _time.GetUtcNow();
        AprsMessage? existing;
        AprsMessage result;
        lock (_lock)
        {
            existing = a.MessageId == null ? null : _messages.LastOrDefault(m => m.Direction == AprsMessageDirection.Incoming
                && m.From == from && m.MessageId == a.MessageId && m.Text == a.MessageText && now - m.Created < TimeSpan.FromMinutes(30));
            result = existing ?? new AprsMessage
            {
                LocalId = ++_nextLocalId, Direction = AprsMessageDirection.Incoming, From = from, To = Normalize(a.Addressee!),
                Text = a.MessageText ?? "", MessageId = a.MessageId, State = AprsMessageState.Received, Created = now,
            };
            if (existing == null) Add(result);
        }
        if (existing == null) Raise(result);

        // A duplicate usually means our ack was lost, so acknowledge again.
        if (AutoAcknowledge && a.MessageId != null && !string.IsNullOrWhiteSpace(MyCall))
        {
            string myCall = MyCall.ToUpperInvariant();
            long id = result.LocalId;
            _ = SendAckAsync(myCall, p.Source, a.MessageId, id);
        }
        return result;
    }

    private async Task SendAckAsync(string myCall, string to, string messageId, long localId)
    {
        try
        {
            await _sender.SendUnprotoAsync(Channel, myCall, Destination, Path, ComposeInfo(to, "ack" + messageId, null)).ConfigureAwait(false);
            Replace(localId, x => x with { AckSent = true });
        }
        catch (Exception ex)
        {
            Replace(localId, x => x with { Error = "Could not send ack: " + ex.Message });
        }
    }

    private AprsMessage? Resolve(string from, string id, AprsMessageState state)
    {
        long? target;
        lock (_lock)
            target = _messages.LastOrDefault(m => m.Direction == AprsMessageDirection.Outgoing && Normalize(m.To) == from && m.MessageId == id
                                                  && m.State is AprsMessageState.Sent or AprsMessageState.Queued or AprsMessageState.GaveUp)?.LocalId;
        if (target == null) return null;
        var now = _time.GetUtcNow();
        return Replace(target.Value, x => x with { State = state, NextAttempt = null, Completed = now });
    }

    private void Add(AprsMessage m)
    {
        _messages.Add(m);
        if (_messages.Count > MaxMessages) _messages.RemoveRange(0, _messages.Count - MaxMessages);
        Interlocked.Increment(ref _version);
    }

    private AprsMessage? Replace(long localId, Func<AprsMessage, AprsMessage> update)
    {
        AprsMessage? updated = null;
        lock (_lock)
        {
            int i = _messages.FindIndex(m => m.LocalId == localId);
            if (i < 0) return null;
            var old = _messages[i];
            updated = update(old);
            if (ReferenceEquals(updated, old)) return old;
            _messages[i] = updated;
            Interlocked.Increment(ref _version);
        }
        Raise(updated);
        return updated;
    }

    private void Raise(AprsMessage m)
    {
        try { MessageChanged?.Invoke(m); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }
}
