using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

#if MACOS

internal sealed partial class MacOsMidiInputPort : IMidiInputPort
{
    private nint _client;
    private nint _port;
    private nint _source;
    private GCHandle _selfHandle;
    private bool _disposed;

    public string Name { get; }
    public bool IsOpen => _port != 0;

    public event Action<MidiMessage>? MessageReceived;

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

    public void Open() { }

    public void Close()
    {
        if (_port == 0) return;
        MIDIPortDisconnectSource(_port, _source);
        MIDIPortDispose(_port);
        _port = 0;
        MIDIClientDispose(_client);
        _client = 0;
    }

    public void Start() { /* CoreMIDI delivers immediately after port creation */ }
    public void Stop() { }

    [UnmanagedCallersOnly]
    private static unsafe void MidiReadCallback(nint packetList, nint readProcRefCon, nint srcConnRefCon)
    {
        var port = (MacOsMidiInputPort?)GCHandle.FromIntPtr(readProcRefCon).Target;
        if (port is null) return;

        // MIDIPacketList: numPackets (UInt32), packets[]
        int numPackets = *(int*)packetList;
        byte* ptr = (byte*)(packetList + 4);

        for (int i = 0; i < numPackets; i++)
        {
            // MIDIPacket: timeStamp (UInt64), length (UInt16), data[]
            long timestamp = *(long*)ptr; ptr += 8;
            ushort length = *(ushort*)ptr; ptr += 2;

            for (int b = 0; b < length; )
            {
                byte status = ptr[b++];
                if ((status & 0x80) == 0) continue; // skip non-status

                byte d1 = b < length ? ptr[b++] : (byte)0;
                byte d2 = b < length ? ptr[b++] : (byte)0;
                port.MessageReceived?.Invoke(new MidiMessage(status, d1, d2, timestamp));
            }
            ptr += length;
            // align to 4 bytes
            long offset = (long)ptr % 4;
            if (offset != 0) ptr += 4 - offset;
        }
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

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

    private static readonly nint kMIDIPropertyName = GetMIDIPropertyName();
    private static nint GetMIDIPropertyName()
    {
        // kMIDIPropertyName is a CFStringRef exported symbol
        var lib = NativeLibrary.Load(CoreMidi);
        NativeLibrary.TryGetExport(lib, "kMIDIPropertyName", out nint ptr);
        return Marshal.ReadIntPtr(ptr);
    }
}

internal sealed partial class MacOsMidiOutputPort : IMidiOutputPort
{
    private nint _client;
    private nint _port;
    private nint _destination;
    private bool _disposed;

    public string Name { get; }
    public bool IsOpen => _port != 0;

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

    public void Open() { }

    public void Close()
    {
        if (_port == 0) return;
        MIDIPortDispose(_port);
        _port = 0;
        MIDIClientDispose(_client);
        _client = 0;
    }

    public void Send(in MidiMessage message)
    {
        if (_port == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            // Build a minimal MIDIPacketList on the stack
            byte* buf = stackalloc byte[32];
            // numPackets
            *(int*)buf = 1;
            // timestamp = 0 (now)
            *(long*)(buf + 4) = 0;
            // length
            *(ushort*)(buf + 12) = 3;
            // data
            buf[14] = message.Status;
            buf[15] = message.Data1;
            buf[16] = message.Data2;
            MIDISend(_port, _destination, (nint)buf);
        }
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

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
