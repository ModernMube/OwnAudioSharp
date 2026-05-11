using System.Runtime.InteropServices;
using System.Text;

namespace OwnAudio.Midi.IO.Platform;

#if LINUX

internal sealed partial class LinuxMidiInputPort : IMidiInputPort
{
    private nint _handle;
    private Thread? _readThread;
    private volatile bool _running;
    private bool _disposed;

    public string Name { get; }
    public bool IsOpen => _handle != 0;

    public event Action<MidiMessage>? MessageReceived;

    public LinuxMidiInputPort(string name, string devicePath)
    {
        Name = name;

        nint dummy;
        int result = snd_rawmidi_open(out _handle, out dummy, devicePath, 0);
        if (result < 0)
            throw new InvalidOperationException($"Failed to open ALSA MIDI input '{devicePath}': {result}");
    }

    public void Open() { }

    public void Close()
    {
        Stop();
        if (_handle == 0) return;
        snd_rawmidi_close(_handle);
        _handle = 0;
    }

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

    public void Stop()
    {
        _running = false;
        _readThread?.Join(TimeSpan.FromSeconds(1));
        _readThread = null;
    }

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
                // 2-byte messages: Program Change, Channel Pressure
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

    public static IReadOnlyList<string> GetInputPortNames()
    {
        // Enumerate /dev/snd/midi* and /dev/midi*
        var names = new List<string>();
        foreach (var path in Directory.GetFiles("/dev", "midi*"))
            names.Add(path);
        foreach (var path in Directory.GetFiles("/dev/snd", "midi*", SearchOption.TopDirectoryOnly))
            names.Add(path);
        return names;
    }

    public static IReadOnlyList<string> GetOutputPortNames() => GetInputPortNames();

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~LinuxMidiInputPort() => Dispose();

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_rawmidi_open(out nint inputp, out nint outputp, string name, int mode);

    [LibraryImport("libasound")]
    private static partial int snd_rawmidi_close(nint rmidi);

    [LibraryImport("libasound")]
    private static unsafe partial long snd_rawmidi_read(nint rmidi, byte* buffer, nuint size);
}

internal sealed partial class LinuxMidiOutputPort : IMidiOutputPort
{
    private nint _handle;
    private bool _disposed;

    public string Name { get; }
    public bool IsOpen => _handle != 0;

    public LinuxMidiOutputPort(string name, string devicePath)
    {
        Name = name;

        nint dummy;
        int result = snd_rawmidi_open(out dummy, out _handle, devicePath, 0);
        if (result < 0)
            throw new InvalidOperationException($"Failed to open ALSA MIDI output '{devicePath}': {result}");
    }

    public void Open() { }

    public void Close()
    {
        if (_handle == 0) return;
        snd_rawmidi_close(_handle);
        _handle = 0;
    }

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

    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        if (_handle == 0) throw new InvalidOperationException("Port not open.");
        unsafe
        {
            fixed (byte* ptr = data)
                snd_rawmidi_write(_handle, ptr, (nuint)data.Length);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~LinuxMidiOutputPort() => Dispose();

    [LibraryImport("libasound", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_rawmidi_open(out nint inputp, out nint outputp, string name, int mode);

    [LibraryImport("libasound")]
    private static partial int snd_rawmidi_close(nint rmidi);

    [LibraryImport("libasound")]
    private static unsafe partial long snd_rawmidi_write(nint rmidi, byte* buffer, nuint size);
}

#endif
