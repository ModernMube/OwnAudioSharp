using System.Runtime.InteropServices;

namespace OwnAudio.Midi.IO.Platform;

/// <summary>
/// Linux virtual MIDI input port implemented via the ALSA Sequencer API (snd_seq_t).
/// Creates a named ALSA sequencer port that external applications can connect to and send MIDI data.
/// </summary>
internal sealed partial class LinuxVirtualMidiInputPort : IMidiInputPort
{
    private const int SND_SEQ_OPEN_DUPLEX = 2;
    private const uint SND_SEQ_PORT_CAP_WRITE = 0x02;
    private const uint SND_SEQ_PORT_CAP_SUBS_WRITE = 0x20;
    private const uint SND_SEQ_PORT_TYPE_MIDI_GENERIC = 0x20;
    private const uint SND_SEQ_PORT_TYPE_APPLICATION = 0x01;

    /// <summary>
    /// ALSA sequencer handle.
    /// </summary>
    private nint _seq;

    /// <summary>
    /// ALSA sequencer port identifier.
    /// </summary>
    private int _port;

    /// <summary>
    /// Background thread that polls for incoming sequencer events.
    /// </summary>
    private Thread? _readThread;

    /// <summary>
    /// Signals the read thread to exit.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Gets the display name of this virtual MIDI input port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the ALSA sequencer handle is open.
    /// </summary>
    public bool IsOpen => _seq != 0;

    /// <summary>
    /// Raised when a complete short MIDI message is received from the sequencer.
    /// </summary>
    public event Action<MidiMessage>? MessageReceived;

    /// <summary>
    /// Raised when a complete SysEx message (0xF0 ... 0xF7) is received.
    /// The span is only valid during the callback invocation.
    /// </summary>
    public event SysExReceivedHandler? SysExReceived;

    /// <summary>
    /// Creates and opens an ALSA sequencer virtual input port with the given name.
    /// Throws <see cref="InvalidOperationException"/> if any ALSA call fails.
    /// </summary>
    public LinuxVirtualMidiInputPort(string name)
    {
        Name = name;

        int result = snd_seq_open(out _seq, "default", SND_SEQ_OPEN_DUPLEX, 0);
        if (result < 0)
            throw new InvalidOperationException($"Failed to open ALSA sequencer: {result}");

        snd_seq_set_client_name(_seq, name);

        _port = snd_seq_create_simple_port(
            _seq, name,
            SND_SEQ_PORT_CAP_WRITE | SND_SEQ_PORT_CAP_SUBS_WRITE,
            SND_SEQ_PORT_TYPE_MIDI_GENERIC | SND_SEQ_PORT_TYPE_APPLICATION);

        if (_port < 0)
        {
            snd_seq_close(_seq);
            _seq = 0;
            throw new InvalidOperationException($"Failed to create ALSA sequencer port: {_port}");
        }
    }

    /// <summary>
    /// No-op — the sequencer port is opened in the constructor.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Stops the read thread and closes the ALSA sequencer handle.
    /// </summary>
    public void Close()
    {
        Stop();
        if (_seq == 0) return;
        snd_seq_close(_seq);
        _seq = 0;
    }

    /// <summary>
    /// Starts the background read thread that delivers incoming MIDI messages from the sequencer.
    /// </summary>
    public void Start()
    {
        if (_seq == 0) throw new InvalidOperationException("Port not open.");
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            Name = $"VirtMidiRead:{Name}",
            IsBackground = true
        };
        _readThread.Start();
    }

    /// <summary>
    /// Signals the read thread to stop and waits up to one second for it to exit.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _readThread?.Join(TimeSpan.FromSeconds(1));
        _readThread = null;
    }

    /// <summary>
    /// Background read loop that calls <c>snd_seq_event_input</c> in a blocking manner
    /// and dispatches each received event to the appropriate event handler.
    /// </summary>
    private void ReadLoop()
    {
        while (_running && _seq != 0)
        {
            int result = snd_seq_event_input(_seq, out nint evPtr);
            if (result < 0 || evPtr == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            unsafe
            {
                var ev = (SndSeqEvent*)evPtr;
                DispatchEvent(ev);
            }
        }
    }

    /// <summary>
    /// Interprets a decoded <see cref="SndSeqEvent"/> and raises the appropriate managed event.
    /// </summary>
    /// <param name="ev">
    /// Pointer to the native ALSA sequencer event structure.
    /// </param>
    private unsafe void DispatchEvent(SndSeqEvent* ev)
    {
        switch (ev->Type)
        {
            case SndSeqEventType.Noteon:
                MessageReceived?.Invoke(new MidiMessage(
                    (byte)(0x90 | (ev->Data[0] & 0x0F)),
                    ev->Data[2],
                    ev->Data[3]));
                break;

            case SndSeqEventType.Noteoff:
                MessageReceived?.Invoke(new MidiMessage(
                    (byte)(0x80 | (ev->Data[0] & 0x0F)),
                    ev->Data[2],
                    ev->Data[3]));
                break;

            case SndSeqEventType.Keypress:
                MessageReceived?.Invoke(new MidiMessage(
                    (byte)(0xA0 | (ev->Data[0] & 0x0F)),
                    ev->Data[2],
                    ev->Data[3]));
                break;

            case SndSeqEventType.Controller:
                MessageReceived?.Invoke(new MidiMessage(
                    (byte)(0xB0 | (ev->Data[0] & 0x0F)),
                    ev->Data[4],
                    ev->Data[5]));
                break;

            case SndSeqEventType.Pgmchange:
                MessageReceived?.Invoke(new MidiMessage(
                    (byte)(0xC0 | (ev->Data[0] & 0x0F)),
                    ev->Data[4],
                    0));
                break;

            case SndSeqEventType.Chanpress:
                MessageReceived?.Invoke(new MidiMessage(
                    (byte)(0xD0 | (ev->Data[0] & 0x0F)),
                    ev->Data[4],
                    0));
                break;

            case SndSeqEventType.Pitchbend:
                int pbVal = *(int*)(ev->Data + 4) + 8192;
                MessageReceived?.Invoke(new MidiMessage(
                    (byte)(0xE0 | (ev->Data[0] & 0x0F)),
                    (byte)(pbVal & 0x7F),
                    (byte)((pbVal >> 7) & 0x7F)));
                break;

            case SndSeqEventType.Sysex:
                nint dataPtr = *(nint*)(ev->Data + 4);
                uint dataLen = *(uint*)(ev->Data);
                if (dataPtr != 0 && dataLen > 0)
                {
                    var span = new ReadOnlySpan<byte>((byte*)dataPtr, (int)dataLen);
                    SysExReceived?.Invoke(span);
                }
                break;

            case SndSeqEventType.Clock:
                MessageReceived?.Invoke(new MidiMessage(0xF8, 0, 0));
                break;

            case SndSeqEventType.Start:
                MessageReceived?.Invoke(new MidiMessage(0xFA, 0, 0));
                break;

            case SndSeqEventType.Continue:
                MessageReceived?.Invoke(new MidiMessage(0xFB, 0, 0));
                break;

            case SndSeqEventType.Stop:
                MessageReceived?.Invoke(new MidiMessage(0xFC, 0, 0));
                break;
        }
    }

    /// <summary>
    /// Stops the read thread and closes the ALSA sequencer handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures the ALSA sequencer handle is released.
    /// </summary>
    ~LinuxVirtualMidiInputPort() => Dispose();

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_open(out nint seq, string name, int streams, int mode);

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_set_client_name(nint seq, string name);

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_create_simple_port(nint seq, string name, uint caps, uint type);

    [LibraryImport("libasound")]
    private static partial int snd_seq_close(nint seq);

    [LibraryImport("libasound")]
    private static partial int snd_seq_event_input(nint seq, out nint ev);
}

/// <summary>
/// Linux virtual MIDI output port implemented via the ALSA Sequencer API (snd_seq_t).
/// Creates a named ALSA sequencer port that this process can write to; external applications
/// can subscribe and receive the MIDI data.
/// </summary>
internal sealed partial class LinuxVirtualMidiOutputPort : IMidiOutputPort
{
    private const int SND_SEQ_OPEN_DUPLEX = 2;
    private const uint SND_SEQ_PORT_CAP_READ = 0x01;
    private const uint SND_SEQ_PORT_CAP_SUBS_READ = 0x02;
    private const uint SND_SEQ_PORT_TYPE_MIDI_GENERIC = 0x20;
    private const uint SND_SEQ_PORT_TYPE_APPLICATION = 0x01;
    private const byte SND_SEQ_QUEUE_DIRECT = 0xFF;

    /// <summary>
    /// ALSA sequencer handle.
    /// </summary>
    private nint _seq;

    /// <summary>
    /// ALSA sequencer port identifier.
    /// </summary>
    private int _port;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Gets the display name of this virtual MIDI output port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the ALSA sequencer handle is open.
    /// </summary>
    public bool IsOpen => _seq != 0;

    /// <summary>
    /// Creates and opens an ALSA sequencer virtual output port with the given name.
    /// Throws <see cref="InvalidOperationException"/> if any ALSA call fails.
    /// </summary>
    public LinuxVirtualMidiOutputPort(string name)
    {
        Name = name;

        int result = snd_seq_open(out _seq, "default", SND_SEQ_OPEN_DUPLEX, 0);
        if (result < 0)
            throw new InvalidOperationException($"Failed to open ALSA sequencer: {result}");

        snd_seq_set_client_name(_seq, name);

        _port = snd_seq_create_simple_port(
            _seq, name,
            SND_SEQ_PORT_CAP_READ | SND_SEQ_PORT_CAP_SUBS_READ,
            SND_SEQ_PORT_TYPE_MIDI_GENERIC | SND_SEQ_PORT_TYPE_APPLICATION);

        if (_port < 0)
        {
            snd_seq_close(_seq);
            _seq = 0;
            throw new InvalidOperationException($"Failed to create ALSA sequencer port: {_port}");
        }
    }

    /// <summary>
    /// No-op — the sequencer port is opened in the constructor.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Closes the ALSA sequencer handle.
    /// </summary>
    public void Close()
    {
        if (_seq == 0) return;
        snd_seq_close(_seq);
        _seq = 0;
    }

    /// <summary>
    /// Sends a short MIDI message to all subscribers of this virtual sequencer port.
    /// </summary>
    public void Send(in MidiMessage message)
    {
        if (_seq == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            SndSeqEvent ev = default;
            ev.Flags = SND_SEQ_QUEUE_DIRECT;

            byte status = message.Status;
            byte type = (byte)(status & 0xF0);
            byte channel = (byte)(status & 0x0F);

            switch (type)
            {
                case 0x90:
                    ev.Type = SndSeqEventType.Noteon;
                    ev.Data[0] = channel;
                    ev.Data[2] = message.Data1;
                    ev.Data[3] = message.Data2;
                    break;
                case 0x80:
                    ev.Type = SndSeqEventType.Noteoff;
                    ev.Data[0] = channel;
                    ev.Data[2] = message.Data1;
                    ev.Data[3] = message.Data2;
                    break;
                case 0xB0:
                    ev.Type = SndSeqEventType.Controller;
                    ev.Data[0] = channel;
                    ev.Data[4] = message.Data1;
                    ev.Data[5] = message.Data2;
                    break;
                case 0xC0:
                    ev.Type = SndSeqEventType.Pgmchange;
                    ev.Data[0] = channel;
                    ev.Data[4] = message.Data1;
                    break;
                case 0xD0:
                    ev.Type = SndSeqEventType.Chanpress;
                    ev.Data[0] = channel;
                    ev.Data[4] = message.Data1;
                    break;
                case 0xE0:
                    ev.Type = SndSeqEventType.Pitchbend;
                    ev.Data[0] = channel;
                    int pbVal = (message.Data1 | (message.Data2 << 7)) - 8192;
                    *(int*)(ev.Data + 4) = pbVal;
                    break;
                default:
                    switch (status)
                    {
                        case 0xF8: ev.Type = SndSeqEventType.Clock; break;
                        case 0xFA: ev.Type = SndSeqEventType.Start; break;
                        case 0xFB: ev.Type = SndSeqEventType.Continue; break;
                        case 0xFC: ev.Type = SndSeqEventType.Stop; break;
                        default: return;
                    }
                    break;
            }

            snd_seq_event_output(_seq, ref ev);
            snd_seq_drain_output(_seq);
        }
    }

    /// <summary>
    /// Sends a SysEx byte sequence to all subscribers of this virtual sequencer port.
    /// </summary>
    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        if (_seq == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            fixed (byte* ptr = data)
            {
                SndSeqEvent ev = default;
                ev.Type = SndSeqEventType.Sysex;
                ev.Flags = (byte)(0x04 | SND_SEQ_QUEUE_DIRECT);
                *(uint*)(ev.Data) = (uint)data.Length;
                *(nint*)(ev.Data + 4) = (nint)ptr;
                snd_seq_event_output(_seq, ref ev);
                snd_seq_drain_output(_seq);
            }
        }
    }

    /// <summary>
    /// Closes the sequencer handle and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures the ALSA sequencer handle is released.
    /// </summary>
    ~LinuxVirtualMidiOutputPort() => Dispose();

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_open(out nint seq, string name, int streams, int mode);

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_set_client_name(nint seq, string name);

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_create_simple_port(nint seq, string name, uint caps, uint type);

    [LibraryImport("libasound")]
    private static partial int snd_seq_close(nint seq);

    [LibraryImport("libasound")]
    private static partial int snd_seq_event_output(nint seq, ref SndSeqEvent ev);

    [LibraryImport("libasound")]
    private static partial int snd_seq_drain_output(nint seq);
}

/// <summary>
/// ALSA sequencer event type identifiers used in the <see cref="SndSeqEvent.Type"/> field.
/// </summary>
internal enum SndSeqEventType : byte
{
    /// <summary>System event: MIDI timing clock (0xF8).</summary>
    Clock = 36,

    /// <summary>System event: MIDI start (0xFA).</summary>
    Start = 30,

    /// <summary>System event: MIDI continue (0xFB).</summary>
    Continue = 31,

    /// <summary>System event: MIDI stop (0xFC).</summary>
    Stop = 32,

    /// <summary>Note-off (0x80) channel message.</summary>
    Noteoff = 7,

    /// <summary>Note-on (0x90) channel message.</summary>
    Noteon = 6,

    /// <summary>Key pressure / aftertouch (0xA0) channel message.</summary>
    Keypress = 8,

    /// <summary>Control change (0xB0) channel message.</summary>
    Controller = 10,

    /// <summary>Program change (0xC0) channel message.</summary>
    Pgmchange = 11,

    /// <summary>Channel pressure (0xD0) channel message.</summary>
    Chanpress = 12,

    /// <summary>Pitch bend (0xE0) channel message.</summary>
    Pitchbend = 13,

    /// <summary>System Exclusive (0xF0) message.</summary>
    Sysex = 130,
}

/// <summary>
/// Managed representation of the native <c>snd_seq_event_t</c> structure used by the ALSA Sequencer API.
/// The <see cref="Data"/> field is a 28-byte fixed buffer that overlays all union members.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SndSeqEvent
{
    /// <summary>
    /// Event type identifier corresponding to one of the <see cref="SndSeqEventType"/> values.
    /// </summary>
    public SndSeqEventType Type;

    /// <summary>
    /// Event flags (e.g. time-stamp type, data encoding).
    /// </summary>
    public byte Flags;

    /// <summary>
    /// Routing tag used internally by the sequencer.
    /// </summary>
    public byte Tag;

    /// <summary>
    /// Queue identifier (0xFF = direct dispatch, no queue).
    /// </summary>
    public byte Queue;

    /// <summary>
    /// 64-bit timestamp field (tick or real-time, selected by <see cref="Flags"/>).
    /// </summary>
    public fixed byte Time[8];

    /// <summary>
    /// Source client/port address (8 bytes).
    /// </summary>
    public fixed byte Source[4];

    /// <summary>
    /// Destination client/port address (8 bytes).
    /// </summary>
    public fixed byte Dest[4];

    /// <summary>
    /// 28-byte data union covering note, control, queue, time-signature, and external data variants.
    /// </summary>
    public fixed byte Data[28];
}
