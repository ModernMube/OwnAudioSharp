using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

#if MACOS

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
    /// for each complete MIDI message found in the packet data.
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

    /// <summary>
    /// kMIDIPropertyName CFStringRef symbol loaded at runtime from the CoreMIDI framework.
    /// </summary>
    private static readonly nint kMIDIPropertyName = GetMIDIPropertyName();

    /// <summary>
    /// Loads the kMIDIPropertyName pointer from the CoreMIDI native library at startup.
    /// </summary>
    private static nint GetMIDIPropertyName()
    {
        var lib = NativeLibrary.Load(CoreMidi);
        NativeLibrary.TryGetExport(lib, "kMIDIPropertyName", out nint ptr);
        return Marshal.ReadIntPtr(ptr);
    }
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
    /// Sends a SysEx buffer by building a single-packet MIDIPacketList on the stack.
    /// </summary>
    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        if (_port == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            int totalSize = 4 + 8 + 2 + data.Length;
            byte* buf = stackalloc byte[totalSize];
            *(int*)buf = 1;
            *(long*)(buf + 4) = 0;
            *(ushort*)(buf + 12) = (ushort)data.Length;
            data.CopyTo(new Span<byte>(buf + 14, data.Length));
            MIDISend(_port, _destination, (nint)buf);
        }
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
}

#endif
