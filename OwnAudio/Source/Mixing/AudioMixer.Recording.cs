using Ownaudio.Audio.Tracks;
using Ownaudio.Core.Common;
using OwnaudioNET.Core;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Capacity of the recording ring buffer expressed in float samples.
    /// Sized to hold approximately four seconds of stereo audio at 48 kHz
    /// (48 000 samples/s × 2 channels × 4 s = 384 000), rounded up to the
    /// nearest power-of-two value required by <see cref="LockFreeRingBuffer{T}"/>.
    /// </summary>
    private const int RecordingRingBufferCapacity = 524_288;

    /// <summary>
    /// The WAV file writer used by the background drain thread to persist audio data.
    /// Created and disposed exclusively inside <c>_recorderLock</c> on the main thread.
    /// </summary>
    private WaveFileWriter? _recorder;

    /// <summary>
    /// Guards <c>_recorder</c> creation and disposal on the main thread.
    /// Never acquired on the real-time audio thread; the mix thread interacts with the
    /// recording pipeline exclusively through <c>_recordingRingBuffer</c>.
    /// </summary>
    private readonly object _recorderLock = new();

    /// <summary>
    /// Indicates whether recording is currently active.
    /// Written on the main thread; read on both the main and audio threads.
    /// Declared <see langword="volatile"/> to guarantee visibility without a lock.
    /// </summary>
    private volatile bool _isRecording;

    /// <summary>
    /// Lock-free single-producer / single-consumer ring buffer that decouples the
    /// real-time mix thread (producer) from the background disk-writer thread (consumer).
    /// Allocated on demand when recording starts; set to <see langword="null"/> on stop.
    /// </summary>
    private LockFreeRingBuffer<float>? _recordingRingBuffer;

    /// <summary>
    /// Background thread that continuously drains <c>_recordingRingBuffer</c> to disk.
    /// Runs at <see cref="ThreadPriority.BelowNormal"/> so it never competes with the
    /// real-time audio thread for CPU time.
    /// </summary>
    private Thread? _recorderDrainThread;

    /// <summary>
    /// Signal flag used to request the drain thread to exit its loop.
    /// Set to <see langword="false"/> when <see cref="StopRecording"/> is called.
    /// Declared <see langword="volatile"/> to guarantee visibility without a lock.
    /// </summary>
    private volatile bool _recorderDrainRunning;

    /// <summary>
    /// Indicates that the active recording drains the native master-output capture
    /// (Rust-native chain) rather than the managed <c>_recordingRingBuffer</c>.
    /// In the Rust-native chain the mix is rendered natively and never passes
    /// through the managed mix thread, so the recorder taps the mixer's native
    /// master-output capture instead. Written and read only under <c>_recorderLock</c>.
    /// </summary>
    private bool _recorderUsesRustCapture;

    /// <summary>
    /// Starts recording the mixed audio output to a WAV file using a lock-free pipeline.
    /// The mix thread pushes samples into a <see cref="LockFreeRingBuffer{T}"/> without
    /// any lock; a dedicated low-priority drain thread reads from that buffer and writes
    /// to disk, ensuring disk I/O never blocks the real-time audio thread.
    /// </summary>
    /// <param name="filePath">Absolute or relative path for the output WAV file.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="filePath"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when recording is already active or when the file cannot be created.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public void StartRecording(string filePath)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        lock (_recorderLock)
        {
            if (_isRecording)
                throw new InvalidOperationException("Already recording. Call StopRecording() first.");

            // Rust-native chain: the mix is rendered natively and never reaches the managed
            // mix thread, so tap the mixer's native master-output capture instead of the
            // managed ring (which WriteToRecorder would fill only in the legacy chain).
            if (_rustNative)
            {
                StartRustCaptureRecording(filePath);
                return;
            }

            try
            {
                _recorder = new WaveFileWriter(filePath, _config);
                _recordingRingBuffer = new LockFreeRingBuffer<float>(RecordingRingBufferCapacity);
                _recorderDrainRunning = true;
                _isRecording = true;

                _recorderDrainThread = new Thread(RecorderDrainLoop)
                {
                    Name = "AudioMixer.RecorderDrain",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };
                _recorderDrainThread.Start();
            }
            catch (Exception ex)
            {
                _recorderDrainRunning = false;
                _isRecording = false;
                _recorder?.Dispose();
                _recorder = null;
                _recordingRingBuffer = null;
                throw new InvalidOperationException($"Failed to start recording: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Starts a Rust-native recording: begins native master-output capture on the shared
    /// session and spins up a drain thread that writes the captured mix to the WAV file.
    /// Must be called while holding <c>_recorderLock</c>.
    /// </summary>
    /// <param name="filePath">Output WAV file path.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when there is no active native session to record, or when capture cannot start.
    /// </exception>
    private void StartRustCaptureRecording(string filePath)
    {
        MultiTrackSession? session;
        lock (_rustSessionLock)
        {
            session = _rustSession;
        }

        if (session is null)
            throw new InvalidOperationException(
                "Cannot record before audio is playing. Add a source and start the mixer, then start recording.");

        try
        {
            _recorder = new WaveFileWriter(filePath, _config);
            session.StartCapture(RecordingRingBufferCapacity);
            _recorderUsesRustCapture = true;
            _recorderDrainRunning = true;
            _isRecording = true;

            _recorderDrainThread = new Thread(RustCaptureDrainLoop)
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
            _recorderUsesRustCapture = false;
            try { session.StopCapture(); } catch { }
            _recorder?.Dispose();
            _recorder = null;
            throw new InvalidOperationException($"Failed to start recording: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Stops an active recording session, signals the drain thread to flush remaining
    /// buffered samples, and closes the WAV file.
    /// Blocks for up to two seconds to allow the drain thread to finish writing.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the mixer has been disposed.</exception>
    public void StopRecording()
    {
        ThrowIfDisposed();

        lock (_recorderLock)
        {
            if (!_isRecording)
                return;

            _recorderDrainRunning = false;
            _isRecording = false;

            _recorderDrainThread?.Join(TimeSpan.FromSeconds(2));
            _recorderDrainThread = null;

            if (_recorderUsesRustCapture)
            {
                StopRustCaptureRecording();
                return;
            }

            try
            {
                _recorder?.Dispose();
            }
            catch { }
            finally
            {
                _recorder = null;
                _recordingRingBuffer = null;
            }
        }
    }

    /// <summary>
    /// Finalises a Rust-native recording after the drain thread has been joined:
    /// flushes any samples still buffered in the native capture ring, stops native
    /// capture, and closes the WAV file. Must be called while holding <c>_recorderLock</c>.
    /// The drain thread is already stopped, so the native ring has a single reader here.
    /// </summary>
    private void StopRustCaptureRecording()
    {
        MultiTrackSession? session;
        lock (_rustSessionLock)
        {
            session = _rustSession;
        }

        try
        {
            if (session is not null && _recorder is not null)
            {
                float[] tail = new float[4096];
                int read;
                while ((read = session.ReadCapture(tail)) > 0)
                {
                    _recorder.WriteSamples(tail.AsSpan(0, read));
                }
            }

            session?.StopCapture();
        }
        catch { }
        finally
        {
            try { _recorder?.Dispose(); } catch { }
            _recorder = null;
            _recorderUsesRustCapture = false;
        }
    }

    /// <summary>
    /// Background drain loop for Rust-native recording: pulls captured master-output
    /// samples from the native session ring and writes them to the WAV file. Runs on a
    /// dedicated low-priority thread and is the sole reader of the capture ring and the
    /// sole writer of <c>_recorder</c> while running, so it takes no lock (it is joined
    /// before <see cref="StopRustCaptureRecording"/> touches either). Sleeps briefly when
    /// the ring is empty to avoid spinning.
    /// </summary>
    private void RustCaptureDrainLoop()
    {
        const int DrainChunkSize = 4096;
        float[] drainBuffer = new float[DrainChunkSize];

        while (_recorderDrainRunning)
        {
            MultiTrackSession? session;
            lock (_rustSessionLock)
            {
                session = _rustSession;
            }

            if (session is null)
                break;

            int read;
            try
            {
                read = session.ReadCapture(drainBuffer);
            }
            catch
            {
                break;
            }

            if (read <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            try
            {
                _recorder?.WriteSamples(drainBuffer.AsSpan(0, read));
            }
            catch
            {
                _recorderDrainRunning = false;
                _isRecording = false;
                break;
            }
        }
    }

    /// <summary>
    /// Pushes mixed audio samples into the recording ring buffer.
    /// This method is zero-allocation and lock-free, making it safe to call
    /// from the real-time audio thread on every mix cycle.
    /// If the ring buffer is full because the drain thread has fallen behind,
    /// samples are silently discarded rather than blocking or allocating.
    /// </summary>
    /// <param name="buffer">The interleaved float samples produced by the current mix cycle.</param>
    private void WriteToRecorder(Span<float> buffer)
    {
        if (!_isRecording) return;

        var ringBuffer = _recordingRingBuffer;
        if (ringBuffer == null) return;

        ringBuffer.Write(buffer);
    }

    /// <summary>
    /// Background drain loop that continuously reads samples from the lock-free ring buffer
    /// and writes them to the WAV file through the <see cref="WaveFileWriter"/>.
    /// Runs on a dedicated low-priority thread; sleeps briefly when the buffer is empty
    /// to avoid spinning and wasting CPU cycles.
    /// The loop exits when <c>_recorderDrainRunning</c> is set to <see langword="false"/>
    /// and the ring buffer has been fully drained.
    /// </summary>
    private void RecorderDrainLoop()
    {
        const int DrainChunkSize = 4096;
        float[] drainBuffer = new float[DrainChunkSize];

        while (_recorderDrainRunning || (_recordingRingBuffer?.Available ?? 0) > 0)
        {
            var ringBuffer = _recordingRingBuffer;
            if (ringBuffer == null) break;

            int available = ringBuffer.Available;
            if (available == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            int toRead = Math.Min(available, DrainChunkSize);
            int read = ringBuffer.Read(drainBuffer.AsSpan(0, toRead));

            if (read > 0)
            {
                lock (_recorderLock)
                {
                    try
                    {
                        _recorder?.WriteSamples(drainBuffer.AsSpan(0, read));
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
    }
}
