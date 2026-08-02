using Logger;
using Ownaudio.Audio.Tracks;
using Ownaudio.Core;

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
    /// Leading interleaved samples still to drop for latency compensation. Set once at start,
    /// counted down by the single writer (drain thread, then the stop-flush), so no lock.
    /// </summary>
    private int _recordingSkipSamples;

    /// <summary>
    /// Frames trimmed off the front of the last recording for latency compensation. 0 when
    /// compensation was off or the backend reported no latency. Handy for logging / verifying.
    /// </summary>
    public int LastRecordingLatencyOffsetFrames { get; private set; }

    /// <summary>
    /// Starts capturing the master output into a WAV file. The mix is rendered natively,
    /// a background thread does the disk I/O so the audio thread never waits on it.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="compensateInputLatency">
    /// When true, drops the input hardware latency (<see cref="IAudioEngine.InputLatencyFrames"/>)
    /// worth of frames off the front, so the take lines up with the moment recording started
    /// instead of trailing the capture pipeline. No-op when input isn't running or the backend
    /// reports no latency. Check <see cref="LastRecordingLatencyOffsetFrames"/> for what was applied.
    /// </param>
    public void StartRecording(string filePath, bool compensateInputLatency = false)
    {
        _throwIfDisposed();

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        lock (_recorderLock)
        {
            if (_isRecording)
                throw new InvalidOperationException("Already recording. Call StopRecording() first.");

            _startRustCaptureRecording(filePath, compensateInputLatency);
        }
    }

    /// <summary>
    /// Turns on native master capture and spins up the drain thread. Call under _recorderLock.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="compensateInputLatency"></param>
    private void _startRustCaptureRecording(string filePath, bool compensateInputLatency)
    {
        MultiTrackSession? _session;
        lock (_rustSessionLock) { _session = _rustSession; }

        if (_session is null)
        {
            Log.Error($"[Recording] Cannot start '{filePath}': no native session, the mixer isn't playing yet");
            throw new InvalidOperationException(
                "Cannot record before audio is playing. Add a source and start the mixer, then start recording.");
        }

        // Grab the latency now, while the stream is live and the number is real.
        int _skipFrames = compensateInputLatency ? _engine.InputLatencyFrames : 0;
        LastRecordingLatencyOffsetFrames = _skipFrames;
        _recordingSkipSamples = _skipFrames * _config.Channels;

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

            Log.Info($"[Recording] Started '{filePath}' at {_config.SampleRate}Hz {_config.Channels}ch, "
                + $"latency trim {_skipFrames} frames");
        }
        catch (Exception ex)
        {
            Log.Error($"[Recording] Start of '{filePath}' failed, rolling back", ex);

            _recorderDrainRunning = false;
            _isRecording = false;

            try { _session.StopCapture(); }
            catch (Exception stopEx) { Log.Error("[Recording] Capture stop during rollback failed too", stopEx); }

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

            if (_recorderDrainThread is { } _drain && !_drain.Join(TimeSpan.FromSeconds(2)))
                Log.Error("[Recording] Drain thread did not finish in 2s, the tail of the take may be missing");

            _recorderDrainThread = null;

            _stopRustCaptureRecording();
            Log.Info("[Recording] Stopped and file closed");
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
                    _writeCapturedSamples(_tail.AsSpan(0, _read));
            }

            _session?.StopCapture();
        }
        catch (Exception ex)
        {
            Log.Error("[Recording] Flushing the capture tail failed, the end of the take may be lost", ex);
        }
        finally
        {
            try { _recorder?.Dispose(); }
            catch (Exception ex) { Log.Error("[Recording] Closing the WAV file failed, it may be corrupt", ex); }

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
            {
                Log.Error("[Recording] Native session vanished under the drain thread, recording ends here");
                break;
            }

            int _read;
            try { _read = _session.ReadCapture(_drain); }
            catch (Exception ex) { Log.Error("[Recording] Capture read failed, drain thread quits", ex); break; }

            if (_read <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            try
            {
                _writeCapturedSamples(_drain.AsSpan(0, _read));
            }
            catch (Exception ex)
            {
                Log.FatalError("[Recording] Disk write failed, recording aborted", ex);
                _recorderDrainRunning = false;
                _isRecording = false;
                break;
            }
        }
    }

    /// <summary>
    /// Writes captured samples to the WAV, eating the latency-compensation pre-roll first.
    /// Single writer at a time (drain thread, then the stop-flush), so the counter needs no lock.
    /// </summary>
    /// <param name="samples"></param>
    private void _writeCapturedSamples(ReadOnlySpan<float> samples)
    {
        if (_recordingSkipSamples > 0)
        {
            if (_recordingSkipSamples >= samples.Length)
            {
                _recordingSkipSamples -= samples.Length;
                return;
            }

            samples = samples.Slice(_recordingSkipSamples);
            _recordingSkipSamples = 0;
        }

        _recorder?.WriteSamples(samples);
    }
}
