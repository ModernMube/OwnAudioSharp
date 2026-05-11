using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

#if WINDOWS

internal sealed partial class WindowsMidiInputPort : IMidiInputPort
{
    private const int CALLBACK_FUNCTION = 0x30000;
    private const int MM_MIM_DATA = 0x3C3;

    private nint _handle;
    private GCHandle _selfHandle;
    private bool _disposed;
    private bool _started;

    public string Name { get; }
    public bool IsOpen => _handle != 0;

    public event Action<MidiMessage>? MessageReceived;

    public WindowsMidiInputPort(string name, int deviceId)
    {
        Name = name;
        _selfHandle = GCHandle.Alloc(this);

        unsafe
        {
            int result = midiInOpen(
                out _handle,
                deviceId,
                &MidiInputCallback,
                GCHandle.ToIntPtr(_selfHandle),
                CALLBACK_FUNCTION);

            if (result != 0)
            {
                _selfHandle.Free();
                throw new InvalidOperationException($"Failed to open MIDI input '{name}': error {result}");
            }
        }
    }

    public void Open() { /* already opened in constructor */ }

    public void Close()
    {
        if (_handle == 0) return;
        if (_started) Stop();
        midiInReset(_handle);
        midiInClose(_handle);
        _handle = 0;
    }

    public void Start()
    {
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        midiInStart(_handle);
        _started = true;
    }

    public void Stop()
    {
        if (_handle == 0 || !_started) return;
        midiInStop(_handle);
        _started = false;
    }

    [UnmanagedCallersOnly]
    private static unsafe void MidiInputCallback(nint handle, int msg, nint instance, nint param1, nint param2)
    {
        if (msg != MM_MIM_DATA) return;

        var port = (WindowsMidiInputPort?)GCHandle.FromIntPtr(instance).Target;
        if (port is null) return;

        byte status = (byte)(param1 & 0xFF);
        byte data1 = (byte)((param1 >> 8) & 0xFF);
        byte data2 = (byte)((param1 >> 16) & 0xFF);
        port.MessageReceived?.Invoke(new MidiMessage(status, data1, data2));
    }

    public static IReadOnlyList<string> GetInputPortNames()
    {
        int count = midiInGetNumDevs();
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var caps = new MIDIINCAPS();
            if (midiInGetDevCaps(i, ref caps, (uint)Marshal.SizeOf<MIDIINCAPS>()) == 0)
                names.Add(caps.szPname);
        }
        return names;
    }

    public static IReadOnlyList<string> GetOutputPortNames()
    {
        int count = midiOutGetNumDevs();
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var caps = new MIDIOUTCAPS();
            if (midiOutGetDevCaps(i, ref caps, (uint)Marshal.SizeOf<MIDIOUTCAPS>()) == 0)
                names.Add(caps.szPname);
        }
        return names;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~WindowsMidiInputPort() => Dispose();

    // P/Invoke declarations
    [LibraryImport("winmm")]
    private static unsafe partial int midiInOpen(
        out nint handle, int deviceId,
        delegate* unmanaged<nint, int, nint, nint, nint, void> callback,
        nint callbackInstance, int flags);

    [LibraryImport("winmm")]
    private static partial int midiInClose(nint handle);

    [LibraryImport("winmm")]
    private static partial int midiInStart(nint handle);

    [LibraryImport("winmm")]
    private static partial int midiInStop(nint handle);

    [LibraryImport("winmm")]
    private static partial int midiInReset(nint handle);

    [LibraryImport("winmm")]
    private static partial int midiInGetNumDevs();

    [LibraryImport("winmm")]
    private static partial int midiInGetDevCaps(int deviceId, ref MIDIINCAPS caps, uint size);

    [LibraryImport("winmm")]
    private static partial int midiOutGetNumDevs();

    [LibraryImport("winmm")]
    private static partial int midiOutGetDevCaps(int deviceId, ref MIDIOUTCAPS caps, uint size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MIDIINCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MIDIOUTCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology, wVoices, wNotes, wChannelMask;
        public uint dwSupport;
    }
}

internal sealed partial class WindowsMidiOutputPort : IMidiOutputPort
{
    private nint _handle;
    private bool _disposed;

    public string Name { get; }
    public bool IsOpen => _handle != 0;

    public WindowsMidiOutputPort(string name, int deviceId)
    {
        Name = name;
        int result = midiOutOpen(out _handle, deviceId, 0, 0, 0);
        if (result != 0)
            throw new InvalidOperationException($"Failed to open MIDI output '{name}': error {result}");
    }

    public void Open() { }

    public void Close()
    {
        if (_handle == 0) return;
        midiOutReset(_handle);
        midiOutClose(_handle);
        _handle = 0;
    }

    public void Send(in MidiMessage message)
    {
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        int packed = message.Status | (message.Data1 << 8) | (message.Data2 << 16);
        midiOutShortMsg(_handle, packed);
    }

    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        // SysEx requires MIDIHDR – simplified: copy to unmanaged and send
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            fixed (byte* ptr = data)
            {
                var header = new MIDIHDR
                {
                    lpData = (nint)ptr,
                    dwBufferLength = (uint)data.Length,
                    dwBytesRecorded = (uint)data.Length
                };
                midiOutPrepareHeader(_handle, ref header, (uint)Marshal.SizeOf<MIDIHDR>());
                midiOutLongMsg(_handle, ref header, (uint)Marshal.SizeOf<MIDIHDR>());
                midiOutUnprepareHeader(_handle, ref header, (uint)Marshal.SizeOf<MIDIHDR>());
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~WindowsMidiOutputPort() => Dispose();

    [LibraryImport("winmm")]
    private static partial int midiOutOpen(out nint handle, int deviceId, nint callback, nint callbackInstance, int flags);

    [LibraryImport("winmm")]
    private static partial int midiOutClose(nint handle);

    [LibraryImport("winmm")]
    private static partial int midiOutShortMsg(nint handle, int msg);

    [LibraryImport("winmm")]
    private static partial int midiOutReset(nint handle);

    [LibraryImport("winmm")]
    private static partial int midiOutPrepareHeader(nint handle, ref MIDIHDR header, uint size);

    [LibraryImport("winmm")]
    private static partial int midiOutUnprepareHeader(nint handle, ref MIDIHDR header, uint size);

    [LibraryImport("winmm")]
    private static partial int midiOutLongMsg(nint handle, ref MIDIHDR header, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIDIHDR
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags;
        public nint lpNext;
        public nint reserved;
        public uint dwOffset;
        public nint dwReserved0, dwReserved1, dwReserved2, dwReserved3;
        public nint dwReserved4, dwReserved5, dwReserved6, dwReserved7;
    }
}

#endif
