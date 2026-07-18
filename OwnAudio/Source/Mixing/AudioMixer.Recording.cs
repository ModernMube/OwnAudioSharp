using Ownaudio.Audio.Tracks;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Native capture ring size in floats — roughly 4s of 48k stereo, rounded to a power of two.
    /// </summary>
    private const int RecordingRingBufferCapacity = 524_288;

    /// <summary>
    /// WAV writer the drain thread pushes into. Created/disposed under _recorderLock only.
    /// </summary>
    private WaveFileWriter? _recorder;

    /// <summary>
    /// Guards _recorder on the main thread. Never taken on the audio thread.
    /// </summary>
    private readonly object _recorderLock = new object();

    /// <summary>
    /// Are we recording? volatile, written on main and read from both sides.
    /// </summary>
    private volatile bool _isRecording;

    /// <summary>
    /// Low-priority thread draining the native capture ring to disk.
    /// </summary>
    private Thread? _recorderDrainThread;

    /// <summary>
    /// Cleared by StopRecording to ask the drain loop to quit.
    /// </summary>
    private volatile bool _recorderDrainRunning;

    /// <summary>
    /// Starts capturing the master output into a WAV file. The mix is rendered natively,
    /// a background thread does the disk I/O so the audio thread never waits on it.
    /// </summary>
    /// <param name="filePath"></param>
    public void StartRecording(string filePath)
    {
        _throwIfDisposed();

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        lock (_recorderLock)
        {
            if (_isRecording)
                throw new InvalidOperationException("Already recording. Call StopRecording() first.");

            _startRustCaptureRecording(filePath);
        }
    }

    /// <summary>
    /// Turns on native master capture and spins up the drain thread. Call under _recorderLock.
    /// </summary>
    /// <param name="filePath"></param>
    private void _startRustCaptureRecording(string filePath)
    {
        MultiTrackSession? _session;
        lock (_rustSessionLock) { _session = _rustSession; }

        if (_session is null)
            throw new InvalidOperationException(
                "Cannot record before audio is playing. Add a source and start the mixer, then start recording.");

        try
        {
            _recorder = new WaveFileWriter(filePath, _config);
            _session.StartCapture(RecordingRingBufferCapacity);
            _recorderDrainRunning = true;
            _isRecording = true;

            _recorderDrainThread = new Thread(_rustCaptureDrainLoop)
            {
                Name = "AudioMixer.RustCaptureDrain",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _recorderDrainThread.Start();
        }
        catch (Exception ex)
        {
            _recorderDrainRunning = false;
            _isRecording = false;
            try { _session.StopCapture(); } catch { }
            _recorder?.Dispose();
            _recorder = null;
            throw new InvalidOperationException($"Failed to start recording: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Stops recording, waits up to 2s for the drain thread and closes the file.
    /// </summary>
    public void StopRecording()
    {
        _throwIfDisposed();

        lock (_recorderLock)
        {
            if (!_isRecording)
                return;

            _recorderDrainRunning = false;
            _isRecording = false;

            _recorderDrainThread?.Join(TimeSpan.FromSeconds(2));
            _recorderDrainThread = null;

            _stopRustCaptureRecording();
        }
    }

    /// <summary>
    /// Flushes whatever is still in the native ring, stops capture and closes the WAV.
    /// The drain thread is already joined here, so we're the only reader.
    /// </summary>
    private void _stopRustCaptureRecording()
    {
        MultiTrackSession? _session;
        lock (_rustSessionLock) { _session = _rustSession; }

        try
        {
            if (_session is not null && _recorder is not null)
            {
                float[] _tail = new float[4096];
                int _read;
                while ((_read = _session.ReadCapture(_tail)) > 0)
                    _recorder.WriteSamples(_tail.AsSpan(0, _read));
            }

            _session?.StopCapture();
        }
        catch { }
        finally
        {
            try { _recorder?.Dispose(); } catch { }
            _recorder = null;
        }
    }

    /// <summary>
    /// Drain loop: pulls captured master samples out of the native ring and writes them.
    /// Sole reader/writer while it runs, so no lock needed; naps a tick when the ring is dry.
    /// </summary>
    private void _rustCaptureDrainLoop()
    {
        float[] _drain = new float[4096];

        while (_recorderDrainRunning)
        {
            MultiTrackSession? _session;
            lock (_rustSessionLock) { _session = _rustSession; }

            if (_session is null)
                break;

            int _read;
            try { _read = _session.ReadCapture(_drain); }
            catch { break; }

            if (_read <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            try
            {
                _recorder?.WriteSamples(_drain.AsSpan(0, _read));
            }
            catch
            {
                _recorderDrainRunning = false;
                _isRecording = false;
                break;
            }
        }
    }
}
