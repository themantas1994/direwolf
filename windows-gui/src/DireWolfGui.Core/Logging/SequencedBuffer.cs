namespace DireWolfGui.Core.Logging;

/// <summary>An item that receives a monotonically increasing sequence number when stored.</summary>
public interface ISequenced
{
    long Sequence { get; set; }
}

/// <summary>
/// Thread-safe bounded ring buffer. Every added item gets a sequence number (1, 2, 3, ...).
/// Consumers poll with <see cref="GetSince"/> passing the last sequence they have seen.
/// </summary>
public sealed class SequencedBuffer<T> where T : class, ISequenced
{
    private readonly T[] _items;
    private readonly object _lock = new();
    private int _start;      // index of oldest item
    private int _count;
    private long _lastSequence;

    public SequencedBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Sequence number of the newest item ever added (0 if none).</summary>
    public long LastSequence { get { lock (_lock) return _lastSequence; } }

    /// <summary>Sequence number of the oldest item still held (0 if empty).</summary>
    public long FirstSequence { get { lock (_lock) return _count == 0 ? 0 : _items[_start].Sequence; } }

    /// <summary>Raised (outside the lock) after an item was added.</summary>
    public event Action<T>? Added;

    public long Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        long seq;
        lock (_lock)
        {
            seq = ++_lastSequence;
            item.Sequence = seq;
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = item;
                _count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }
        Added?.Invoke(item);
        return seq;
    }

    /// <summary>Items with sequence greater than <paramref name="afterSequence"/>, oldest first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<T> GetSince(long afterSequence, int max = int.MaxValue)
    {
        lock (_lock)
        {
            if (_count == 0 || max <= 0) return Array.Empty<T>();
            long first = _items[_start].Sequence;
            int skip = afterSequence < first ? 0 : (int)Math.Min(_count, afterSequence - first + 1);
            int n = Math.Min(_count - skip, max);
            var result = new T[n];
            for (int i = 0; i < n; i++) result[i] = _items[(_start + skip + i) % _items.Length];
            return result;
        }
    }

    /// <summary>The newest <paramref name="max"/> items, oldest first.</summary>
    public IReadOnlyList<T> GetLatest(int max)
    {
        lock (_lock)
        {
            int n = Math.Min(_count, Math.Max(0, max));
            var result = new T[n];
            for (int i = 0; i < n; i++) result[i] = _items[(_start + _count - n + i) % _items.Length];
            return result;
        }
    }

    public IReadOnlyList<T> Snapshot() => GetSince(0);

    /// <summary>Removes all items. Sequence numbers keep increasing so pollers never see reuse.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_items);
            _start = 0;
            _count = 0;
        }
    }
}
