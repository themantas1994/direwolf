using System.Runtime.InteropServices;

namespace DireWolfGui.Infrastructure;

/// <summary>An audio device as Dire Wolf for Windows sees it (WinMM waveIn / waveOut).</summary>
public sealed record AudioDevice(int Number, string Name, int Channels, bool IsInput)
{
    public override string ToString() => $"{Number}: {Name}";
}

/// <summary>
/// Audio devices through the same WinMM API that Dire Wolf for Windows uses
/// (src/audio_win.c), so device numbers and names match what Dire Wolf accepts in
/// ADEVICE: a number, or a case-sensitive part of the name (names are at most 31
/// characters, as WinMM reports them).
/// </summary>
public static class WinMmAudio
{
    public static IReadOnlyList<AudioDevice> InputDevices()
    {
        var list = new List<AudioDevice>();
        if (!OperatingSystem.IsWindows()) return list;
        var n = waveInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            if (waveInGetDevCapsW((nint)i, out var caps, (uint)Marshal.SizeOf<WAVEINCAPSW>()) == 0)
                list.Add(new AudioDevice((int)i, caps.szPname, caps.wChannels, true));
        }
        return list;
    }

    public static IReadOnlyList<AudioDevice> OutputDevices()
    {
        var list = new List<AudioDevice>();
        if (!OperatingSystem.IsWindows()) return list;
        var n = waveOutGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            if (waveOutGetDevCapsW((nint)i, out var caps, (uint)Marshal.SizeOf<WAVEOUTCAPSW>()) == 0)
                list.Add(new AudioDevice((int)i, caps.szPname, caps.wChannels, false));
        }
        return list;
    }

    /// <summary>
    /// The value to write in ADEVICE for a device: its name when that name selects it
    /// uniquely (names survive devices being added; numbers can shift), else its number.
    /// Dire Wolf uses the first device whose name contains the text.
    /// </summary>
    public static string AdeviceToken(AudioDevice d, IReadOnlyList<AudioDevice> all)
    {
        var first = all.FirstOrDefault(x => x.Name.Contains(d.Name, StringComparison.Ordinal));
        if (first == d && !d.Name.Contains('"') && d.Name.Trim().Length > 0)
            return d.Name.Contains(' ') ? "\"" + d.Name + "\"" : d.Name;
        return d.Number.ToString();
    }

    // ---- P/Invoke ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEINCAPSW
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels, wReserved1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEOUTCAPSW
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels, wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetDevCapsW(nint id, out WAVEINCAPSW caps, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveOutGetDevCapsW(nint id, out WAVEOUTCAPSW caps, uint size);
}

/// <summary>
/// Live input level of one WinMM device, for setting the receive level before Dire Wolf
/// runs.  Only listens: never opens an output device and never keys a transmitter.
/// Peak level is reported in percent of full scale, per channel.
/// </summary>
public sealed class WaveInLevelMeter : IDisposable
{
    private const int SampleRate = 44100;
    private const int Buffers = 4;
    private const int BufferBytes = 4410 * 2 * 2; // 100 ms, 16 bit, up to stereo
    private nint _handle;
    private readonly nint[] _headers = new nint[Buffers];
    private readonly nint[] _data = new nint[Buffers];
    private System.Threading.Timer? _poll;
    private readonly int _channels;
    private readonly object _lock = new();

    /// <summary>Peak levels (0-100 %) for left/mono and right.</summary>
    public event Action<double, double>? Level;

    public WaveInLevelMeter(int deviceNumber, int channels)
    {
        _channels = Math.Clamp(channels, 1, 2);
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = (ushort)_channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = (ushort)(_channels * 2),
            nAvgBytesPerSec = (uint)(SampleRate * _channels * 2),
        };
        var r = waveInOpen(out _handle, (nint)deviceNumber, ref fmt, 0, 0, 0);
        if (r != 0) throw new InvalidOperationException($"The audio device could not be opened for listening (WinMM error {r}). It may be in use exclusively by another program, or disabled.");
        var hdrSize = Marshal.SizeOf<WAVEHDR>();
        for (var i = 0; i < Buffers; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferBytes);
            _headers[i] = Marshal.AllocHGlobal(hdrSize);
            var h = new WAVEHDR { lpData = _data[i], dwBufferLength = (uint)(SampleRate / 10 * 2 * _channels) };
            Marshal.StructureToPtr(h, _headers[i], false);
            waveInPrepareHeader(_handle, _headers[i], (uint)hdrSize);
            waveInAddBuffer(_handle, _headers[i], (uint)hdrSize);
        }
        waveInStart(_handle);
        _poll = new System.Threading.Timer(_ => Poll(), null, 50, 50);
    }

    private void Poll()
    {
        lock (_lock)
        {
            if (_handle == 0) return;
            var hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
            for (var i = 0; i < Buffers; i++)
            {
                var h = Marshal.PtrToStructure<WAVEHDR>(_headers[i]);
                if ((h.dwFlags & 1) == 0) continue; // WHDR_DONE
                var samples = (int)h.dwBytesRecorded / 2;
                int peakL = 0, peakR = 0;
                for (var s = 0; s < samples; s++)
                {
                    var v = Math.Abs((int)Marshal.ReadInt16(_data[i], s * 2));
                    if (_channels == 2 && (s & 1) == 1) peakR = Math.Max(peakR, v);
                    else peakL = Math.Max(peakL, v);
                }
                Level?.Invoke(peakL * 100.0 / 32768, (_channels == 2 ? peakR : peakL) * 100.0 / 32768);
                waveInUnprepareHeader(_handle, _headers[i], hdrSize);
                h.dwFlags = 0;
                h.dwBytesRecorded = 0;
                Marshal.StructureToPtr(h, _headers[i], false);
                waveInPrepareHeader(_handle, _headers[i], hdrSize);
                waveInAddBuffer(_handle, _headers[i], hdrSize);
            }
        }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        lock (_lock)
        {
            if (_handle == 0) return;
            waveInStop(_handle);
            waveInReset(_handle);
            var hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
            for (var i = 0; i < Buffers; i++)
            {
                waveInUnprepareHeader(_handle, _headers[i], hdrSize);
                Marshal.FreeHGlobal(_headers[i]);
                Marshal.FreeHGlobal(_data[i]);
            }
            waveInClose(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public nint lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags, dwLoops;
        public nint lpNext, reserved;
    }

    [DllImport("winmm.dll")] private static extern uint waveInOpen(out nint h, nint id, ref WAVEFORMATEX fmt, nint cb, nint inst, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(nint h, nint hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(nint h, nint hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(nint h, nint hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(nint h);
    [DllImport("winmm.dll")] private static extern uint waveInStop(nint h);
    [DllImport("winmm.dll")] private static extern uint waveInReset(nint h);
    [DllImport("winmm.dll")] private static extern uint waveInClose(nint h);
}
