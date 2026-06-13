using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

/// <summary>
/// Windows MIDI input port implemented via the winmm midiIn* API.
/// Opens the device in the constructor using an unmanaged function pointer callback.
/// </summary>
internal sealed partial class WindowsMidiInputPort : IMidiInputPort
{
    private const int CALLBACK_FUNCTION = 0x30000;
    private const int MM_MIM_DATA = 0x3C3;
    private const int MM_MIM_LONGDATA = 0x3C4;
    private const int SysExBufferSize = 4096;
    private const int SysExBufferCount = 4;

    /// <summary>
    /// Native winmm device handle.
    /// </summary>
    private nint _handle;

    /// <summary>
    /// GCHandle keeping this instance alive for the unmanaged callback.
    /// </summary>
    private GCHandle _selfHandle;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Indicates whether <see cref="Start"/> has been called without a matching <see cref="Stop"/>.
    /// </summary>
    private bool _started;

    /// <summary>
    /// Unmanaged pointers to the MIDIHDR structures allocated for SysEx input buffers.
    /// </summary>
    private nint[] _sysexHeaderPtrs = new nint[SysExBufferCount];

    /// <summary>
    /// Unmanaged pointers to the raw data buffers referenced by each MIDIHDR.
    /// </summary>
    private nint[] _sysexBufferPtrs = new nint[SysExBufferCount];

    /// <summary>
    /// Set to true during <see cref="Close"/> to prevent re-queuing buffers from the callback.
    /// </summary>
    private volatile bool _closing;

    /// <summary>
    /// Gets the display name of this MIDI input port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the native device handle is open.
    /// </summary>
    public bool IsOpen => _handle != 0;

    /// <summary>
    /// Raised on the winmm callback thread when a short MIDI message arrives.
    /// </summary>
    public event Action<MidiMessage>? MessageReceived;

    /// <summary>
    /// Raised on the winmm callback thread when a complete SysEx message has been received.
    /// The span is only valid during the callback invocation.
    /// </summary>
    public event SysExReceivedHandler? SysExReceived;

    /// <summary>
    /// Opens the MIDI input device identified by <paramref name="deviceId"/> using an unmanaged callback.
    /// Throws <see cref="InvalidOperationException"/> if the device cannot be opened.
    /// </summary>
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

    /// <summary>
    /// No-op — the port is already opened in the constructor.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Stops listening, unprepares all SysEx headers, frees unmanaged memory, and closes the native handle.
    /// </summary>
    public void Close()
    {
        if (_handle == 0) return;
        _closing = true;
        if (_started) Stop();
        midiInReset(_handle);

        unsafe
        {
            uint hdrSize = (uint)sizeof(InputMIDIHDR);
            for (int i = 0; i < SysExBufferCount; i++)
            {
                if (_sysexHeaderPtrs[i] != 0)
                {
                    midiInUnprepareHeader(_handle, _sysexHeaderPtrs[i], hdrSize);
                    Marshal.FreeHGlobal(_sysexHeaderPtrs[i]);
                    _sysexHeaderPtrs[i] = 0;
                }
                if (_sysexBufferPtrs[i] != 0)
                {
                    Marshal.FreeHGlobal(_sysexBufferPtrs[i]);
                    _sysexBufferPtrs[i] = 0;
                }
            }
        }

        midiInClose(_handle);
        _handle = 0;
        _closing = false;
    }

    /// <summary>
    /// Begins delivery of MIDI input messages via <see cref="MessageReceived"/> and allocates SysEx receive buffers.
    /// </summary>
    public void Start()
    {
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        midiInStart(_handle);
        _started = true;
        PrepareSysExBuffers();
    }

    /// <summary>
    /// Allocates four unmanaged SysEx receive buffers, prepares each MIDIHDR, and submits them to the driver.
    /// </summary>
    private unsafe void PrepareSysExBuffers()
    {
        uint hdrSize = (uint)sizeof(InputMIDIHDR);
        for (int i = 0; i < SysExBufferCount; i++)
        {
            nint bufPtr = Marshal.AllocHGlobal(SysExBufferSize);
            nint hdrPtr = Marshal.AllocHGlobal((int)hdrSize);
            System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned((void*)hdrPtr, 0, hdrSize);

            var hdr = (InputMIDIHDR*)hdrPtr;
            hdr->lpData = bufPtr;
            hdr->dwBufferLength = SysExBufferSize;
            hdr->dwBytesRecorded = 0;
            hdr->dwFlags = 0;

            _sysexBufferPtrs[i] = bufPtr;
            _sysexHeaderPtrs[i] = hdrPtr;

            midiInPrepareHeader(_handle, hdrPtr, hdrSize);
            midiInAddBuffer(_handle, hdrPtr, hdrSize);
        }
    }

    /// <summary>
    /// Pauses delivery of MIDI input messages.
    /// </summary>
    public void Stop()
    {
        if (_handle == 0 || !_started) return;
        midiInStop(_handle);
        _started = false;
    }

    /// <summary>
    /// Unmanaged callback invoked by winmm on the driver thread for each incoming MIDI message.
    /// Handles both short channel messages (MM_MIM_DATA) and long SysEx messages (MM_MIM_LONGDATA).
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe void MidiInputCallback(nint handle, int msg, nint instance, nint param1, nint param2)
    {
        var port = (WindowsMidiInputPort?)GCHandle.FromIntPtr(instance).Target;
        if (port is null) return;

        if (msg == MM_MIM_DATA)
        {
            byte status = (byte)(param1 & 0xFF);
            byte data1 = (byte)((param1 >> 8) & 0xFF);
            byte data2 = (byte)((param1 >> 16) & 0xFF);
            port.MessageReceived?.Invoke(new MidiMessage(status, data1, data2));
        }
        else if (msg == MM_MIM_LONGDATA)
        {
            var headerPtr = (InputMIDIHDR*)param1;
            if (headerPtr != null && headerPtr->dwBytesRecorded > 0)
            {
                var span = new ReadOnlySpan<byte>((byte*)headerPtr->lpData, (int)headerPtr->dwBytesRecorded);
                port.SysExReceived?.Invoke(span);
            }

            if (!port._closing)
                midiInAddBuffer(handle, param1, (uint)sizeof(InputMIDIHDR));
        }
    }

    /// <summary>
    /// Returns the names of all installed MIDI input devices on this system.
    /// </summary>
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

    /// <summary>
    /// Returns the names of all installed MIDI output devices on this system.
    /// </summary>
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

    /// <summary>
    /// Closes the port and releases the GCHandle used by the unmanaged callback.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures the native handle and GCHandle are released.
    /// </summary>
    ~WindowsMidiInputPort() => Dispose();

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

    [DllImport("winmm")]
    private static extern int midiInGetDevCaps(int deviceId, ref MIDIINCAPS caps, uint size);

    [LibraryImport("winmm")]
    private static partial int midiInPrepareHeader(nint handle, nint headerPtr, uint size);

    [LibraryImport("winmm")]
    private static partial int midiInUnprepareHeader(nint handle, nint headerPtr, uint size);

    [LibraryImport("winmm")]
    private static partial int midiInAddBuffer(nint handle, nint headerPtr, uint size);

    [LibraryImport("winmm")]
    private static partial int midiOutGetNumDevs();

    [DllImport("winmm")]
    private static extern int midiOutGetDevCaps(int deviceId, ref MIDIOUTCAPS caps, uint size);

    /// <summary>
    /// Native capability structure for MIDI input devices (MIDIINCAPS).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MIDIINCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwSupport;
    }

    /// <summary>
    /// Native capability structure for MIDI output devices (MIDIOUTCAPS).
    /// </summary>
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

    /// <summary>
    /// Native MIDI header structure (MIDIHDR) used for SysEx input buffer management.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InputMIDIHDR
    {
        /// <summary>
        /// Pointer to the unmanaged data buffer.
        /// </summary>
        public nint lpData;

        /// <summary>
        /// Length of the data buffer in bytes.
        /// </summary>
        public uint dwBufferLength;

        /// <summary>
        /// Number of bytes recorded into the buffer by the driver.
        /// </summary>
        public uint dwBytesRecorded;

        /// <summary>
        /// User-defined data value.
        /// </summary>
        public nint dwUser;

        /// <summary>
        /// Flags set by the driver indicating buffer state.
        /// </summary>
        public uint dwFlags;

        /// <summary>
        /// Reserved for driver use: pointer to next header in queue.
        /// </summary>
        public nint lpNext;

        /// <summary>
        /// Reserved for driver use.
        /// </summary>
        public nint reserved;

        /// <summary>
        /// Offset into the buffer of the next event to be processed.
        /// </summary>
        public uint dwOffset;

        /// <summary>
        /// Reserved fields for driver internal use (slots 0–7).
        /// </summary>
        public nint dwReserved0, dwReserved1, dwReserved2, dwReserved3;

        /// <summary>
        /// Reserved fields for driver internal use (slots 4–7).
        /// </summary>
        public nint dwReserved4, dwReserved5, dwReserved6, dwReserved7;
    }
}

/// <summary>
/// Windows MIDI output port implemented via the winmm midiOut* API.
/// Supports short messages and SysEx transmission via MIDIHDR.
/// </summary>
internal sealed partial class WindowsMidiOutputPort : IMidiOutputPort
{
    /// <summary>
    /// Native winmm device handle.
    /// </summary>
    private nint _handle;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Gets the display name of this MIDI output port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the native device handle is open.
    /// </summary>
    public bool IsOpen => _handle != 0;

    /// <summary>
    /// Opens the MIDI output device identified by <paramref name="deviceId"/>.
    /// Throws <see cref="InvalidOperationException"/> if the device cannot be opened.
    /// </summary>
    public WindowsMidiOutputPort(string name, int deviceId)
    {
        Name = name;
        int result = midiOutOpen(out _handle, deviceId, 0, 0, 0);
        if (result != 0)
            throw new InvalidOperationException($"Failed to open MIDI output '{name}': error {result}");
    }

    /// <summary>
    /// No-op — the port is already opened in the constructor.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Resets and closes the native MIDI output handle.
    /// </summary>
    public void Close()
    {
        if (_handle == 0) return;
        midiOutReset(_handle);
        midiOutClose(_handle);
        _handle = 0;
    }

    /// <summary>
    /// Sends a short MIDI message packed into a single 32-bit integer to the output device.
    /// </summary>
    public void Send(in MidiMessage message)
    {
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        int packed = message.Status | (message.Data1 << 8) | (message.Data2 << 16);
        midiOutShortMsg(_handle, packed);
    }

    /// <summary>
    /// Sends a SysEx buffer to the output device using MIDIHDR prepare/send/unprepare.
    /// </summary>
    public void SendSysEx(ReadOnlySpan<byte> data)
    {
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
                midiOutPrepareHeader(_handle, ref header, (uint)sizeof(MIDIHDR));
                midiOutLongMsg(_handle, ref header, (uint)sizeof(MIDIHDR));
                midiOutUnprepareHeader(_handle, ref header, (uint)sizeof(MIDIHDR));
            }
        }
    }

    /// <summary>
    /// Closes the port and releases the native device handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures the native handle is released.
    /// </summary>
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

    /// <summary>
    /// Native MIDI header structure (MIDIHDR) used for SysEx transmission.
    /// </summary>
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
