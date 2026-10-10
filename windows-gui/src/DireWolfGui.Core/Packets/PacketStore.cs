using DireWolfGui.Core.Logging;

namespace DireWolfGui.Core.Packets;

/// <summary>Per-channel packet counters.</summary>
public sealed record ChannelActivity(int Channel, long Received, long Transmitted, DateTimeOffset? LastReceived, DateTimeOffset? LastTransmitted, int? LastAudioLevel);

/// <summary>Bounded packet history plus per-channel receive/transmit counters. Thread-safe.</summary>
public sealed class PacketStore
{
    private readonly SequencedBuffer<PacketRecord> _buffer;
    private readonly Dictionary<int, ChannelActivity> _channels = new();
    private readonly object _lock = new();

    public PacketStore(int capacity = 5000) => _buffer = new SequencedBuffer<PacketRecord>(capacity);

    public SequencedBuffer<PacketRecord> Buffer => _buffer;
    public long LastSequence => _buffer.LastSequence;

    public event Action<PacketRecord>? Added
    {
        add => _buffer.Added += value;
        remove => _buffer.Added -= value;
    }

    public long Add(PacketRecord packet)
    {
        if (packet.Channel is int ch)
        {
            lock (_lock)
            {
                _channels.TryGetValue(ch, out var a);
                a ??= new ChannelActivity(ch, 0, 0, null, null, null);
                _channels[ch] = packet.Direction == PacketDirection.Received
                    ? a with { Received = a.Received + 1, LastReceived = packet.Time, LastAudioLevel = packet.AudioLevel ?? a.LastAudioLevel }
                    : a with { Transmitted = a.Transmitted + 1, LastTransmitted = packet.Time };
            }
        }
        return _buffer.Add(packet);
    }

    public IReadOnlyList<PacketRecord> GetSince(long afterSequence, int max = int.MaxValue) => _buffer.GetSince(afterSequence, max);
    public IReadOnlyList<PacketRecord> GetLatest(int max) => _buffer.GetLatest(max);
    public IReadOnlyList<PacketRecord> Snapshot() => _buffer.Snapshot();

    public IReadOnlyList<ChannelActivity> GetChannelActivity()
    {
        lock (_lock) return _channels.Values.OrderBy(c => c.Channel).ToArray();
    }

    public void Clear()
    {
        _buffer.Clear();
        lock (_lock) _channels.Clear();
    }
}
