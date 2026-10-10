using System.Buffers.Binary;
using System.Text;

namespace DireWolfGui.Core.Net;

/// <summary>
/// One AGW PE protocol message: 36-byte header (port, kind, PID, call from/to, data length, user) + data.
/// </summary>
public sealed record AgwFrame(int Port, char DataKind, string CallFrom = "", string CallTo = "", byte[]? Data = null, byte Pid = 0, uint User = 0)
{
    public const int HeaderLength = 36;
    /// <summary>Largest data length accepted from a server; anything larger is treated as a protocol error.</summary>
    public const int MaxDataLength = 64 * 1024;

    public byte[] Payload => Data ?? Array.Empty<byte>();

    /// <summary>Data as text with trailing NULs removed (Dire Wolf includes the C string terminator).</summary>
    public string DataText => Encoding.UTF8.GetString(Payload).TrimEnd('\0');

    public byte[] Encode()
    {
        var p = Payload;
        var buf = new byte[HeaderLength + p.Length];
        buf[0] = (byte)Port;
        buf[4] = (byte)DataKind;
        buf[6] = Pid;
        WriteCall(buf.AsSpan(8, 10), CallFrom);
        WriteCall(buf.AsSpan(18, 10), CallTo);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), (uint)p.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32), User);
        p.CopyTo(buf, HeaderLength);
        return buf;
    }

    private static void WriteCall(Span<byte> dest, string call)
    {
        var bytes = Encoding.ASCII.GetBytes(call ?? "");
        bytes.AsSpan(0, Math.Min(bytes.Length, dest.Length - 1)).CopyTo(dest);   // keep a terminating NUL
    }

    private static string ReadCall(ReadOnlySpan<byte> src)
    {
        int n = src.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? src : src[..n]).Trim();
    }

    /// <summary>Decodes the 36-byte header. Returns the frame (without data) and the announced data length.</summary>
    public static AgwFrame DecodeHeader(ReadOnlySpan<byte> header, out uint dataLength)
    {
        if (header.Length < HeaderLength) throw new ArgumentException("AGW header must be 36 bytes.", nameof(header));
        dataLength = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        return new AgwFrame(header[0], (char)header[4], ReadCall(header.Slice(8, 10)), ReadCall(header.Slice(18, 10)),
            null, header[6], BinaryPrimitives.ReadUInt32LittleEndian(header[32..]));
    }

    /// <summary>Decodes one complete message from <paramref name="buffer"/>; returns false if more bytes are needed.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out AgwFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;
        if (buffer.Length < HeaderLength) return false;
        var h = DecodeHeader(buffer, out uint len);
        if (len > MaxDataLength) throw new InvalidDataException($"AGW data length {len} exceeds limit.");
        if (buffer.Length < HeaderLength + len) return false;
        frame = h with { Data = buffer.Slice(HeaderLength, (int)len).ToArray() };
        consumed = HeaderLength + (int)len;
        return true;
    }

    public override string ToString() => $"AGW '{DataKind}' port {Port} {CallFrom}>{CallTo} len {Payload.Length}";
}

/// <summary>Port list from a 'G' reply, e.g. "2;Port1 stdin soundcard mono;Port2 ...;".</summary>
public sealed record AgwPortInfo(int Count, IReadOnlyList<string> Descriptions)
{
    public static AgwPortInfo Parse(string text)
    {
        var parts = text.TrimEnd('\0').Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out int n)) return new AgwPortInfo(0, Array.Empty<string>());
        return new AgwPortInfo(n, parts.Skip(1).ToArray());
    }
}
