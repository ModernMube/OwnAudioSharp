using System.Runtime.InteropServices;
using System.Text;

namespace OwnAudio.Midi.IO.Platform;

#if LINUX

/// <summary>
/// Linux MIDI input port using ALSA rawmidi (libasound snd_rawmidi_*).
/// Reads bytes on a background thread and assembles MIDI messages with running-status support.
/// </summary>
internal sealed partial class LinuxMidiInputPort : IMidiInputPort
{
    /// <summary>
    /// ALSA rawmidi read handle.
    /// </summary>
    private nint _handle;

    /// <summary>
    /// Background thread that reads raw bytes from the ALSA device.
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
    /// Gets the display name or device path of this MIDI input port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the ALSA handle is open.
    /// </summary>
    public bool IsOpen => _handle != 0;

    /// <summary>
    /// Raised on the read thread when a complete MIDI message has been assembled.
    /// </summary>
    public event Action<MidiMessage>? MessageReceived;

    /// <summary>
    /// Opens the ALSA rawmidi input at <paramref name="devicePath"/>.
    /// Throws <see cref="InvalidOperationException"/> if the device cannot be opened.
    /// </summary>
    public LinuxMidiInputPort(string name, string devicePath)
    {
        Name = name;

        nint dummy;
        int result = snd_rawmidi_open(out _handle, out dummy, devicePath, 0);
        if (result < 0)
            throw new InvalidOperationException($"Failed to open ALSA MIDI input '{devicePath}': {result}");
    }

    /// <summary>
    /// No-op — the device is already opened in the constructor.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Stops the read thread and closes the ALSA handle.
    /// </summary>
    public void Close()
    {
        Stop();
        if (_handle == 0) return;
        snd_rawmidi_close(_handle);
        _handle = 0;
    }

    /// <summary>
    /// Starts the background read thread that delivers incoming MIDI messages.
    /// </summary>
    public void Start()
    {
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            Name = $"MidiRead:{Name}",
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
    /// Read loop executed on the background thread: reads raw bytes from the ALSA device
    /// and assembles MIDI messages using running-status rules.
    /// </summary>
    private unsafe void ReadLoop()
    {
        byte* buf = stackalloc byte[256];
        byte runningStatus = 0;

        while (_running && _handle != 0)
        {
            long bytesRead = snd_rawmidi_read(_handle, buf, 256);
            if (bytesRead <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            int pos = 0;
            while (pos < bytesRead)
            {
                byte b = buf[pos++];
                if ((b & 0x80) != 0) runningStatus = b;

                if (runningStatus == 0) continue;

                byte type = (byte)(runningStatus & 0xF0);
                if (type == 0xC0 || type == 0xD0)
                {
                    byte d1 = pos < bytesRead ? buf[pos++] : (byte)0;
                    MessageReceived?.Invoke(new MidiMessage(runningStatus, d1, 0));
                }
                else
                {
                    byte d1 = pos < bytesRead ? buf[pos++] : (byte)0;
                    byte d2 = pos < bytesRead ? buf[pos++] : (byte)0;
                    MessageReceived?.Invoke(new MidiMessage(runningStatus, d1, d2));
                }
            }
        }
    }

    /// <summary>
    /// Returns all MIDI device paths found under /dev and /dev/snd matching the "midi*" pattern.
    /// </summary>
    public static IReadOnlyList<string> GetInputPortNames()
    {
        var names = new List<string>();
        foreach (var path in Directory.GetFiles("/dev", "midi*"))
            names.Add(path);
        foreach (var path in Directory.GetFiles("/dev/snd", "midi*", SearchOption.TopDirectoryOnly))
            names.Add(path);
        return names;
    }

    /// <summary>
    /// Returns the same device list as <see cref="GetInputPortNames"/> — rawmidi devices are bidirectional.
    /// </summary>
    public static IReadOnlyList<string> GetOutputPortNames() => GetInputPortNames();

    /// <summary>
    /// Stops the read thread and closes the ALSA handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures the ALSA handle is released.
    /// </summary>
    ~LinuxMidiInputPort() => Dispose();

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_rawmidi_open(out nint inputp, out nint outputp, string name, int mode);

    [LibraryImport("libasound")]
    private static partial int snd_rawmidi_close(nint rmidi);

    [LibraryImport("libasound")]
    private static unsafe partial long snd_rawmidi_read(nint rmidi, byte* buffer, nuint size);
}

/// <summary>
/// Linux MIDI output port using ALSA rawmidi (libasound snd_rawmidi_*).
/// Writes MIDI messages directly as raw bytes with correct length for the message type.
/// </summary>
internal sealed partial class LinuxMidiOutputPort : IMidiOutputPort
{
    /// <summary>
    /// ALSA rawmidi write handle.
    /// </summary>
    private nint _handle;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Gets the display name or device path of this MIDI output port.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the ALSA handle is open.
    /// </summary>
    public bool IsOpen => _handle != 0;

    /// <summary>
    /// Opens the ALSA rawmidi output at <paramref name="devicePath"/>.
    /// Throws <see cref="InvalidOperationException"/> if the device cannot be opened.
    /// </summary>
    public LinuxMidiOutputPort(string name, string devicePath)
    {
        Name = name;

        nint dummy;
        int result = snd_rawmidi_open(out dummy, out _handle, devicePath, 0);
        if (result < 0)
            throw new InvalidOperationException($"Failed to open ALSA MIDI output '{devicePath}': {result}");
    }

    /// <summary>
    /// No-op — the device is already opened in the constructor.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// Closes the ALSA rawmidi handle.
    /// </summary>
    public void Close()
    {
        if (_handle == 0) return;
        snd_rawmidi_close(_handle);
        _handle = 0;
    }

    /// <summary>
    /// Writes a short MIDI message to the ALSA device; sends 2 bytes for Program Change
    /// and Channel Pressure, 3 bytes for all other message types.
    /// </summary>
    public void Send(in MidiMessage message)
    {
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            byte* buf = stackalloc byte[3];
            buf[0] = message.Status;
            buf[1] = message.Data1;
            buf[2] = message.Data2;
            byte type = (byte)(message.Status & 0xF0);
            int len = (type == 0xC0 || type == 0xD0) ? 2 : 3;
            snd_rawmidi_write(_handle, buf, (nuint)len);
        }
    }

    /// <summary>
    /// Writes a raw SysEx byte buffer directly to the ALSA device.
    /// </summary>
    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            fixed (byte* ptr = data)
                snd_rawmidi_write(_handle, ptr, (nuint)data.Length);
        }
    }

    /// <summary>
    /// Closes the ALSA handle and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures the ALSA handle is released.
    /// </summary>
    ~LinuxMidiOutputPort() => Dispose();

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_rawmidi_open(out nint inputp, out nint outputp, string name, int mode);

    [LibraryImport("libasound")]
    private static partial int snd_rawmidi_close(nint rmidi);

    [LibraryImport("libasound")]
    private static unsafe partial long snd_rawmidi_write(nint rmidi, byte* buffer, nuint size);
}

#endif
