using System.Buffers;
using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

/// <summary>
/// macOS MIDI input port implemented via the CoreMIDI framework.
/// Receives messages through an unmanaged MIDIReadProc callback registered on a CoreMIDI input port.
/// </summary>
internal sealed partial class MacOsMidiInputPort : IMidiInputPort
{
    /// <summary>
    /// CoreMIDI client reference created for this port.
    /// </summary>
    private nint _client;

    /// <summary>
    /// CoreMIDI input port reference.
    /// </summary>
    private nint _port;

    /// <summary>
    /// CoreMIDI source endpoint this port is connected to.
    /// </summary>
    private nint _source;

    /// <summary>
    /// GCHandle keeping this instance alive for the unmanaged MIDIReadProc callback.
    /// </summary>
    private GCHandle _selfHandle;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Indicates whether the callback parser is currently inside a SysEx message spanning packets.
    /// </summary>
    private bool _inSysEx;

    /// <summary>
    /// Accumulation buffer for incoming SysEx bytes (maximum 64 KB).
    /// </summary>
    private readonly byte[] _sysexBuf = new byte[65536];

    /// <summary>
    /// Number of bytes currently stored in <see cref="_sysexBuf"/>.
    /// </summary>
    private int _sysexIdx;

    /// <summary>
    /// Gets the display name of this MIDI input port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the CoreMIDI port reference is valid.
    /// </summary>
    public bool IsOpen => _port != 0;

    /// <summary>
    /// Raised on the CoreMIDI callback thread when MIDI packets arrive.
    /// </summary>
    public event Action<MidiMessage>? MessageReceived;

    /// <summary>
    /// Raised on the CoreMIDI callback thread when a complete SysEx message (0xF0 ... 0xF7) has been received.
    /// The span is only valid during the callback invocation.
    /// </summary>
    public event SysExReceivedHandler? SysExReceived;

    /// <summary>
    /// Creates a CoreMIDI client, opens an input port, and connects it to <paramref name="sourceEndpoint"/>.
    /// Throws <see cref="InvalidOperationException"/> if any CoreMIDI call fails.
    /// </summary>
    public MacOsMidiInputPort(string name, nint sourceEndpoint)
    {
        Name = name;
        _source = sourceEndpoint;
        _selfHandle = GCHandle.Alloc(this);

        unsafe
        {
            nint clientName = CFStringCreateWithCString(0, "OwnAudio.Midi", 0x08000100);
            int err = MIDIClientCreate(clientName, null, 0, out _client);
            CFRelease(clientName);

            if (err != 0)
            {
                _selfHandle.Free();
                throw new InvalidOperationException($"Failed to create CoreMIDI client: {err}");
            }

            nint portName = CFStringCreateWithCString(0, name, 0x08000100);
            err = MIDIInputPortCreate(_client, portName, &MidiReadCallback,
                GCHandle.ToIntPtr(_selfHandle), out _port);
            CFRelease(portName);

            if (err != 0)
            {
                MIDIClientDispose(_client);
                _selfHandle.Free();
                throw new InvalidOperationException($"Failed to create CoreMIDI input port: {err}");
            }

            MIDIPortConnectSource(_port, sourceEndpoint, 0);
        }
    }

    /// <summary>
    /// No-op — the port is connected to its source in the constructor.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Disconnects the source, disposes the CoreMIDI port, and releases the client.
    /// </summary>
    public void Close()
    {
        if (_port == 0) return;
        MIDIPortDisconnectSource(_port, _source);
        MIDIPortDispose(_port);
        _port = 0;
        MIDIClientDispose(_client);
        _client = 0;
    }

    /// <summary>
    /// No-op — CoreMIDI delivers messages immediately after port creation and connection.
    /// </summary>
    public void Start() { }

    /// <summary>
    /// No-op — message delivery stops when the port is closed or disposed.
    /// </summary>
    public void Stop() { }

    /// <summary>
    /// CoreMIDI read callback that unpacks MIDIPacketList structures and raises <see cref="MessageReceived"/>
    /// or <see cref="SysExReceived"/> for each complete message found in the packet data.
    /// SysEx messages that span multiple packets are accumulated across calls.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe void MidiReadCallback(nint packetList, nint readProcRefCon, nint srcConnRefCon)
    {
        var port = (MacOsMidiInputPort?)GCHandle.FromIntPtr(readProcRefCon).Target;
        if (port is null) return;

        int numPackets = *(int*)packetList;
        byte* ptr = (byte*)(packetList + 4);

        for (int i = 0; i < numPackets; i++)
        {
            long timestamp = *(long*)ptr; ptr += 8;
            ushort length = *(ushort*)ptr; ptr += 2;

            for (int b = 0; b < length; )
            {
                byte status = ptr[b++];

                if (status == 0xF0)
                {
                    port._inSysEx = true;
                    port._sysexIdx = 0;
                    if (port._sysexIdx < port._sysexBuf.Length)
                        port._sysexBuf[port._sysexIdx++] = status;
                    continue;
                }

                if (port._inSysEx)
                {
                    if (status == 0xF7)
                    {
                        if (port._sysexIdx < port._sysexBuf.Length)
                            port._sysexBuf[port._sysexIdx++] = status;
                        port.SysExReceived?.Invoke(port._sysexBuf.AsSpan(0, port._sysexIdx));
                        port._inSysEx = false;
                    }
                    else
                    {
                        if (port._sysexIdx < port._sysexBuf.Length)
                            port._sysexBuf[port._sysexIdx++] = status;
                    }
                    continue;
                }

                if ((status & 0x80) == 0) continue;

                byte d1 = b < length ? ptr[b++] : (byte)0;
                byte d2 = b < length ? ptr[b++] : (byte)0;
                port.MessageReceived?.Invoke(new MidiMessage(status, d1, d2, timestamp));
            }
            ptr += length;

            long offset = (long)ptr % 4;
            if (offset != 0) ptr += 4 - offset;
        }
    }

    /// <summary>
    /// Returns the names of all CoreMIDI source endpoints available on this system.
    /// </summary>
    public static IReadOnlyList<string> GetInputPortNames()
    {
        int count = (int)MIDIGetNumberOfSources();
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            nint src = MIDIGetSource((nint)i);
            nint nameRef;
            MIDIObjectGetStringProperty(src, kMIDIPropertyName, out nameRef);
            var name = Marshal.PtrToStringAuto(nameRef) ?? $"MIDI Input {i}";
            CFRelease(nameRef);
            names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Returns the names of all CoreMIDI destination endpoints available on this system.
    /// </summary>
    public static IReadOnlyList<string> GetOutputPortNames()
    {
        int count = (int)MIDIGetNumberOfDestinations();
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            nint dest = MIDIGetDestination((nint)i);
            nint nameRef;
            MIDIObjectGetStringProperty(dest, kMIDIPropertyName, out nameRef);
            var name = Marshal.PtrToStringAuto(nameRef) ?? $"MIDI Output {i}";
            CFRelease(nameRef);
            names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Closes the port, releases the GCHandle, and suppresses finalization.
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
    /// Finalizer that ensures CoreMIDI resources are released.
    /// </summary>
    ~MacOsMidiInputPort() => Dispose();

    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreMidi)]
    private static unsafe partial int MIDIClientCreate(nint name,
        delegate* unmanaged<nint, nint, void> notifyProc, nint notifyRefCon, out nint client);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIClientDispose(nint client);

    [LibraryImport(CoreMidi)]
    private static unsafe partial int MIDIInputPortCreate(nint client, nint portName,
        delegate* unmanaged<nint, nint, nint, void> readProc, nint refCon, out nint port);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIPortConnectSource(nint port, nint source, nint connRefCon);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIPortDisconnectSource(nint port, nint source);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIPortDispose(nint port);

    [LibraryImport(CoreMidi)]
    private static partial nint MIDIGetNumberOfSources();

    [LibraryImport(CoreMidi)]
    private static partial nint MIDIGetSource(nint index);

    [LibraryImport(CoreMidi)]
    private static partial nint MIDIGetNumberOfDestinations();

    [LibraryImport(CoreMidi)]
    private static partial nint MIDIGetDestination(nint index);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIObjectGetStringProperty(nint obj, nint propertyID, out nint value);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);

    private static readonly Lazy<nint> s_kMIDIPropertyName =
        new Lazy<nint>(() =>
        {
            var lib = NativeLibrary.Load(CoreMidi);
            NativeLibrary.TryGetExport(lib, "kMIDIPropertyName", out nint ptr);
            return Marshal.ReadIntPtr(ptr);
        });

    private static nint kMIDIPropertyName => s_kMIDIPropertyName.Value;
}

/// <summary>
/// macOS MIDI output port implemented via the CoreMIDI framework.
/// Sends short MIDI messages and SysEx data by building MIDIPacketList structures on the stack.
/// </summary>
internal sealed partial class MacOsMidiOutputPort : IMidiOutputPort
{
    /// <summary>
    /// CoreMIDI client reference created for this port.
    /// </summary>
    private nint _client;

    /// <summary>
    /// CoreMIDI output port reference.
    /// </summary>
    private nint _port;

    /// <summary>
    /// CoreMIDI destination endpoint this port sends to.
    /// </summary>
    private nint _destination;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Gets the display name of this MIDI output port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the CoreMIDI port reference is valid.
    /// </summary>
    public bool IsOpen => _port != 0;

    /// <summary>
    /// Scaling factor to convert nanoseconds to Mach absolute time units.
    /// Computed once from <c>mach_timebase_info</c> at class initialisation.
    /// </summary>
    private static readonly double s_nsToMachScale;

    /// <summary>
    /// Initialises the Mach time scaling factor by querying <c>mach_timebase_info</c>.
    /// </summary>
    static MacOsMidiOutputPort()
    {
        mach_timebase_info(out MachTimebaseInfo info);
        s_nsToMachScale = info.denom == 0 ? 1.0 : (double)info.denom / (double)info.numer;
    }

    /// <summary>
    /// Creates a CoreMIDI client and output port connected to <paramref name="destinationEndpoint"/>.
    /// Throws <see cref="InvalidOperationException"/> if any CoreMIDI call fails.
    /// </summary>
    public MacOsMidiOutputPort(string name, nint destinationEndpoint)
    {
        Name = name;
        _destination = destinationEndpoint;

        unsafe
        {
            nint clientName = CFStringCreateWithCString(0, "OwnAudio.Midi.Out", 0x08000100);
            int err = MIDIClientCreate(clientName, null, 0, out _client);
            CFRelease(clientName);

            if (err != 0) throw new InvalidOperationException($"Failed to create CoreMIDI client: {err}");

            nint portName = CFStringCreateWithCString(0, name, 0x08000100);
            err = MIDIOutputPortCreate(_client, portName, out _port);
            CFRelease(portName);

            if (err != 0)
            {
                MIDIClientDispose(_client);
                throw new InvalidOperationException($"Failed to create CoreMIDI output port: {err}");
            }
        }
    }

    /// <summary>
    /// No-op — the port is ready to send immediately after construction.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Disposes the CoreMIDI port and client references.
    /// </summary>
    public void Close()
    {
        if (_port == 0) return;
        MIDIPortDispose(_port);
        _port = 0;
        MIDIClientDispose(_client);
        _client = 0;
    }

    /// <summary>
    /// Sends a short MIDI message by building a single-packet MIDIPacketList on the stack.
    /// </summary>
    public void Send(in MidiMessage message)
    {
        if (_port == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            byte* buf = stackalloc byte[32];
            *(int*)buf = 1;
            *(long*)(buf + 4) = 0;
            *(ushort*)(buf + 12) = 3;
            buf[14] = message.Status;
            buf[15] = message.Data1;
            buf[16] = message.Data2;
            MIDISend(_port, _destination, (nint)buf);
        }
    }

    /// <summary>
    /// Sends a short MIDI message with an absolute nanosecond timestamp.
    /// The timestamp is converted to Mach absolute time before being embedded in the MIDIPacketList.
    /// Pass zero for <paramref name="timestampNs"/> to send immediately.
    /// </summary>
    /// <param name="message">
    /// The MIDI message to send.
    /// </param>
    /// <param name="timestampNs">
    /// Absolute send time in nanoseconds, or zero for immediate delivery.
    /// </param>
    public void Send(in MidiMessage message, long timestampNs)
    {
        if (_port == 0) throw new InvalidOperationException("Port not open.");
        long machTimestamp = timestampNs > 0 ? (long)(timestampNs * s_nsToMachScale) : 0;
        unsafe
        {
            byte* buf = stackalloc byte[32];
            *(int*)buf = 1;
            *(long*)(buf + 4) = machTimestamp;
            *(ushort*)(buf + 12) = 3;
            buf[14] = message.Status;
            buf[15] = message.Data1;
            buf[16] = message.Data2;
            MIDISend(_port, _destination, (nint)buf);
        }
    }

    /// <summary>
    /// Sends a SysEx buffer by building a single-packet MIDIPacketList.
    /// Uses stack allocation for payloads up to 4 KB; rents from <see cref="ArrayPool{T}"/> for larger data.
    /// </summary>
    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        if (_port == 0) throw new InvalidOperationException("Port not open.");
        const int stackThreshold = 4096;
        int totalSize = 14 + data.Length;

        if (totalSize <= stackThreshold)
        {
            unsafe
            {
                byte* buf = stackalloc byte[totalSize];
                BuildAndSendSysExPacket(buf, data);
            }
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(totalSize);
            try
            {
                unsafe
                {
                    fixed (byte* buf = rented)
                        BuildAndSendSysExPacket(buf, data);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Writes a MIDIPacketList header and SysEx payload into <paramref name="buf"/> and calls MIDISend.
    /// </summary>
    /// <param name="buf">
    /// Pointer to a buffer of at least <c>14 + data.Length</c> bytes.
    /// </param>
    /// <param name="data">
    /// The SysEx payload bytes to embed in the packet.
    /// </param>
    private unsafe void BuildAndSendSysExPacket(byte* buf, ReadOnlySpan<byte> data)
    {
        *(int*)buf = 1;
        *(long*)(buf + 4) = 0;
        *(ushort*)(buf + 12) = (ushort)data.Length;
        data.CopyTo(new Span<byte>(buf + 14, data.Length));
        MIDISend(_port, _destination, (nint)buf);
    }

    /// <summary>
    /// Closes the port and releases all CoreMIDI resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures CoreMIDI resources are released.
    /// </summary>
    ~MacOsMidiOutputPort() => Dispose();

    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreMidi)]
    private static unsafe partial int MIDIClientCreate(nint name,
        delegate* unmanaged<nint, nint, void> notifyProc, nint notifyRefCon, out nint client);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIClientDispose(nint client);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIOutputPortCreate(nint client, nint portName, out nint port);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIPortDispose(nint port);

    [LibraryImport(CoreMidi)]
    private static partial int MIDISend(nint port, nint dest, nint packetList);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);

    [LibraryImport(CoreFoundation)]
    private static partial int mach_timebase_info(out MachTimebaseInfo info);

    /// <summary>
    /// Native structure returned by <c>mach_timebase_info</c> describing the Mach clock frequency.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebaseInfo
    {
        /// <summary>
        /// Numerator of the numer/denom fraction converting Mach units to nanoseconds.
        /// </summary>
        public uint numer;

        /// <summary>
        /// Denominator of the numer/denom fraction converting Mach units to nanoseconds.
        /// </summary>
        public uint denom;
    }
}

/// <summary>
/// macOS virtual MIDI input port that creates a CoreMIDI destination endpoint.
/// External applications connect to this destination and send MIDI data that is received here.
/// </summary>
internal sealed partial class MacOsVirtualMidiInputPort : IMidiInputPort
{
    /// <summary>
    /// CoreMIDI client reference created for this virtual port.
    /// </summary>
    private nint _client;

    /// <summary>
    /// CoreMIDI virtual destination endpoint.
    /// </summary>
    private nint _destination;

    /// <summary>
    /// GCHandle keeping this instance alive for the unmanaged MIDIReadProc callback.
    /// </summary>
    private GCHandle _selfHandle;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Indicates whether the callback parser is currently inside a SysEx message spanning packets.
    /// </summary>
    private bool _inSysEx;

    /// <summary>
    /// Accumulation buffer for incoming SysEx bytes (maximum 64 KB).
    /// </summary>
    private readonly byte[] _sysexBuf = new byte[65536];

    /// <summary>
    /// Number of bytes currently stored in <see cref="_sysexBuf"/>.
    /// </summary>
    private int _sysexIdx;

    /// <summary>
    /// Gets the display name of this virtual MIDI input port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the CoreMIDI destination endpoint is valid.
    /// </summary>
    public bool IsOpen => _destination != 0;

    /// <summary>
    /// Raised on the CoreMIDI callback thread when a short MIDI message arrives.
    /// </summary>
    public event Action<MidiMessage>? MessageReceived;

    /// <summary>
    /// Raised on the CoreMIDI callback thread when a complete SysEx message (0xF0 ... 0xF7) has been received.
    /// The span is only valid during the callback invocation.
    /// </summary>
    public event SysExReceivedHandler? SysExReceived;

    /// <summary>
    /// Creates a CoreMIDI virtual destination endpoint with the given name.
    /// External applications can connect to this endpoint and send MIDI data.
    /// Throws <see cref="InvalidOperationException"/> if any CoreMIDI call fails.
    /// </summary>
    public MacOsVirtualMidiInputPort(string name)
    {
        Name = name;
        _selfHandle = GCHandle.Alloc(this);

        unsafe
        {
            nint clientName = CFStringCreateWithCString(0, "OwnAudio.Midi.VIn", 0x08000100);
            int err = MIDIClientCreate(clientName, null, 0, out _client);
            CFRelease(clientName);

            if (err != 0)
            {
                _selfHandle.Free();
                throw new InvalidOperationException($"Failed to create CoreMIDI client: {err}");
            }

            nint portName = CFStringCreateWithCString(0, name, 0x08000100);
            err = MIDIDestinationCreate(_client, portName, &MidiReadCallback,
                GCHandle.ToIntPtr(_selfHandle), out _destination);
            CFRelease(portName);

            if (err != 0)
            {
                MIDIClientDispose(_client);
                _selfHandle.Free();
                throw new InvalidOperationException($"Failed to create CoreMIDI virtual destination: {err}");
            }
        }
    }

    /// <summary>
    /// No-op — the virtual destination is ready immediately after construction.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Disposes the CoreMIDI destination endpoint and client.
    /// </summary>
    public void Close()
    {
        if (_destination == 0) return;
        MIDIEndpointDispose(_destination);
        _destination = 0;
        MIDIClientDispose(_client);
        _client = 0;
    }

    /// <summary>
    /// No-op — CoreMIDI delivers messages immediately after the destination is created.
    /// </summary>
    public void Start() { }

    /// <summary>
    /// No-op — message delivery stops when the destination is disposed.
    /// </summary>
    public void Stop() { }

    /// <summary>
    /// CoreMIDI read callback that unpacks incoming MIDIPacketList data and raises the appropriate events.
    /// SysEx messages that span multiple packets are accumulated across calls.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe void MidiReadCallback(nint packetList, nint readProcRefCon, nint srcConnRefCon)
    {
        var port = (MacOsVirtualMidiInputPort?)GCHandle.FromIntPtr(readProcRefCon).Target;
        if (port is null) return;

        int numPackets = *(int*)packetList;
        byte* ptr = (byte*)(packetList + 4);

        for (int i = 0; i < numPackets; i++)
        {
            long timestamp = *(long*)ptr; ptr += 8;
            ushort length = *(ushort*)ptr; ptr += 2;

            for (int b = 0; b < length; )
            {
                byte status = ptr[b++];

                if (status == 0xF0)
                {
                    port._inSysEx = true;
                    port._sysexIdx = 0;
                    if (port._sysexIdx < port._sysexBuf.Length)
                        port._sysexBuf[port._sysexIdx++] = status;
                    continue;
                }

                if (port._inSysEx)
                {
                    if (status == 0xF7)
                    {
                        if (port._sysexIdx < port._sysexBuf.Length)
                            port._sysexBuf[port._sysexIdx++] = status;
                        port.SysExReceived?.Invoke(port._sysexBuf.AsSpan(0, port._sysexIdx));
                        port._inSysEx = false;
                    }
                    else
                    {
                        if (port._sysexIdx < port._sysexBuf.Length)
                            port._sysexBuf[port._sysexIdx++] = status;
                    }
                    continue;
                }

                if ((status & 0x80) == 0) continue;

                byte d1 = b < length ? ptr[b++] : (byte)0;
                byte d2 = b < length ? ptr[b++] : (byte)0;
                port.MessageReceived?.Invoke(new MidiMessage(status, d1, d2, timestamp));
            }
            ptr += length;

            long offset = (long)ptr % 4;
            if (offset != 0) ptr += 4 - offset;
        }
    }

    /// <summary>
    /// Closes the virtual destination and releases all CoreMIDI resources.
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
    /// Finalizer that ensures CoreMIDI resources are released.
    /// </summary>
    ~MacOsVirtualMidiInputPort() => Dispose();

    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreMidi)]
    private static unsafe partial int MIDIClientCreate(nint name,
        delegate* unmanaged<nint, nint, void> notifyProc, nint notifyRefCon, out nint client);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIClientDispose(nint client);

    [LibraryImport(CoreMidi)]
    private static unsafe partial int MIDIDestinationCreate(nint client, nint name,
        delegate* unmanaged<nint, nint, nint, void> readProc, nint refCon, out nint outDest);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIEndpointDispose(nint endpoint);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);
}

/// <summary>
/// macOS virtual MIDI output port that creates a CoreMIDI source endpoint.
/// This process pushes MIDI data to the source; external applications subscribe and receive it.
/// </summary>
internal sealed partial class MacOsVirtualMidiOutputPort : IMidiOutputPort
{
    /// <summary>
    /// CoreMIDI client reference created for this virtual port.
    /// </summary>
    private nint _client;

    /// <summary>
    /// CoreMIDI virtual source endpoint.
    /// </summary>
    private nint _source;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Gets the display name of this virtual MIDI output port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the CoreMIDI source endpoint is valid.
    /// </summary>
    public bool IsOpen => _source != 0;

    /// <summary>
    /// Creates a CoreMIDI virtual source endpoint with the given name.
    /// External applications can subscribe to this source to receive MIDI data.
    /// Throws <see cref="InvalidOperationException"/> if any CoreMIDI call fails.
    /// </summary>
    public MacOsVirtualMidiOutputPort(string name)
    {
        Name = name;

        unsafe
        {
            nint clientName = CFStringCreateWithCString(0, "OwnAudio.Midi.VOut", 0x08000100);
            int err = MIDIClientCreate(clientName, null, 0, out _client);
            CFRelease(clientName);

            if (err != 0) throw new InvalidOperationException($"Failed to create CoreMIDI client: {err}");

            nint portName = CFStringCreateWithCString(0, name, 0x08000100);
            err = MIDISourceCreate(_client, portName, out _source);
            CFRelease(portName);

            if (err != 0)
            {
                MIDIClientDispose(_client);
                throw new InvalidOperationException($"Failed to create CoreMIDI virtual source: {err}");
            }
        }
    }

    /// <summary>
    /// No-op — the virtual source is ready immediately after construction.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Disposes the CoreMIDI source endpoint and client.
    /// </summary>
    public void Close()
    {
        if (_source == 0) return;
        MIDIEndpointDispose(_source);
        _source = 0;
        MIDIClientDispose(_client);
        _client = 0;
    }

    /// <summary>
    /// Sends a short MIDI message to all subscribers of this virtual source.
    /// </summary>
    public void Send(in MidiMessage message)
    {
        if (_source == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            byte* buf = stackalloc byte[32];
            *(int*)buf = 1;
            *(long*)(buf + 4) = 0;
            *(ushort*)(buf + 12) = 3;
            buf[14] = message.Status;
            buf[15] = message.Data1;
            buf[16] = message.Data2;
            MIDIReceived(_source, (nint)buf);
        }
    }

    /// <summary>
    /// Sends a SysEx byte sequence to all subscribers of this virtual source.
    /// Uses stack allocation for payloads up to 4 KB; rents from <see cref="ArrayPool{T}"/> for larger data.
    /// </summary>
    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        if (_source == 0) throw new InvalidOperationException("Port not open.");
        const int stackThreshold = 4096;
        int totalSize = 14 + data.Length;

        if (totalSize <= stackThreshold)
        {
            unsafe
            {
                byte* buf = stackalloc byte[totalSize];
                BuildAndSendSysExPacket(buf, data);
            }
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(totalSize);
            try
            {
                unsafe
                {
                    fixed (byte* buf = rented)
                        BuildAndSendSysExPacket(buf, data);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Writes a MIDIPacketList header and SysEx payload into <paramref name="buf"/> and calls MIDIReceived.
    /// </summary>
    /// <param name="buf">
    /// Pointer to a buffer of at least <c>14 + data.Length</c> bytes.
    /// </param>
    /// <param name="data">
    /// The SysEx payload bytes to embed in the packet.
    /// </param>
    private unsafe void BuildAndSendSysExPacket(byte* buf, ReadOnlySpan<byte> data)
    {
        *(int*)buf = 1;
        *(long*)(buf + 4) = 0;
        *(ushort*)(buf + 12) = (ushort)data.Length;
        data.CopyTo(new Span<byte>(buf + 14, data.Length));
        MIDIReceived(_source, (nint)buf);
    }

    /// <summary>
    /// Closes the virtual source and releases all CoreMIDI resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures CoreMIDI resources are released.
    /// </summary>
    ~MacOsVirtualMidiOutputPort() => Dispose();

    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreMidi)]
    private static unsafe partial int MIDIClientCreate(nint name,
        delegate* unmanaged<nint, nint, void> notifyProc, nint notifyRefCon, out nint client);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIClientDispose(nint client);

    [LibraryImport(CoreMidi)]
    private static partial int MIDISourceCreate(nint client, nint name, out nint outSrc);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIEndpointDispose(nint endpoint);

    [LibraryImport(CoreMidi)]
    private static partial int MIDIReceived(nint src, nint packetList);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);
}
