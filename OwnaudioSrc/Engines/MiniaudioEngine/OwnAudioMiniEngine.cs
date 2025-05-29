using Ownaudio.Exceptions;
using Ownaudio.MiniAudio;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ownaudio.Engines;

/// <summary>
/// Interact with audio devices using separate MiniAudio engines for playback and capture.
/// This class cannot be inherited.
/// <para>Implements: <see cref="IAudioEngine"/>.</para>
/// </summary>
public sealed class OwnAudioMiniEngine : IAudioEngine
{
    private readonly AudioEngineOutputOptions _outOptions;
    private readonly AudioEngineInputOptions _inOptions;
    private readonly MiniAudioEngine? _playbackEngine;
    private readonly MiniAudioEngine? _captureEngine;
    private readonly int _framesPerBuffer;
    private bool _disposed;

    private readonly ConcurrentQueue<float[]> _inputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly ConcurrentQueue<float[]> _outputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly ConcurrentBag<float[]> _inputBufferPool = new ConcurrentBag<float[]>();
    private readonly ConcurrentBag<float[]> _outputBufferPool = new ConcurrentBag<float[]>();
    private readonly int _maxQueueSize = 10;

    private Stopwatch _stopwatch = new Stopwatch();

    private GCHandle? _playbackEngineHandle;
    private GCHandle? _captureEngineHandle;

    /// <summary>
    /// Initializes a new instance of OwnAudioMiniEngine with only output options.
    /// </summary>
    /// <param name="outOptions">Optional audio engine output options.</param>
    /// <param name="framesPerBuffer">The number of frames in the buffer (default 512).</param>
    /// <exception cref="MiniaudioException">
    /// Thrown if an error occurs during MiniAudio initialization.
    /// </exception>
    public OwnAudioMiniEngine(AudioEngineOutputOptions? outOptions = default, int framesPerBuffer = 512)
        : this(null, outOptions, framesPerBuffer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OwnAudioMiniEngine class with both input and output options.
    /// </summary>
    /// <param name="inOptions">Optional audio engine input options.</param>
    /// <param name="outOptions">Optional audio engine output options.</param>
    /// <param name="framesPerBuffer">Number of frames per buffer (default is 512).</param>
    /// <exception cref="MiniaudioException">
    /// Thrown when errors occur during MiniAudio initialization.
    /// </exception>
    public OwnAudioMiniEngine(AudioEngineInputOptions? inOptions = default, AudioEngineOutputOptions? outOptions = default, int framesPerBuffer = 512)
    {
        _inOptions = inOptions ?? new AudioEngineInputOptions();
        _outOptions = outOptions ?? new AudioEngineOutputOptions();
        _framesPerBuffer = framesPerBuffer;

        if (_outOptions.Channels > 0)
        {
            _playbackEngine = new MiniAudioEngine(
                sampleRate: (int)_outOptions.SampleRate,
                deviceType: EngineDeviceType.Playback,
                sampleFormat: EngineAudioFormat.F32,
                channels: (int)_outOptions.Channels
            );

            SetupPlaybackProcessing();
        }

        if (_inOptions.Channels > 0)
        {
            _captureEngine = new MiniAudioEngine(
                sampleRate: (int)_inOptions.SampleRate,
                deviceType: EngineDeviceType.Capture,
                sampleFormat: EngineAudioFormat.F32,
                channels: (int)_inOptions.Channels
            );

            SetupCaptureProcessing();
        }

        InitializeBufferPools();
    }

    /// <summary>
    /// Sets up the audio processing callback for the playback engine.
    /// </summary>
    private void SetupPlaybackProcessing()
    {
        if (_playbackEngine == null) return;

        _playbackEngine.AudioProcessing += (sender, args) =>
        {
            if (args.Direction == AudioDataDirection.Output)
            {
                ProcessOutput(args);
            }
        };
    }

    /// <summary>
    /// Sets up the audio processing callback for the capture engine.
    /// </summary>
    private void SetupCaptureProcessing()
    {
        if (_captureEngine == null) return;

        _captureEngine.AudioProcessing += (sender, args) =>
        {
            if (args.Direction == AudioDataDirection.Input)
            {
                ProcessInput(args);
            }
        };
    }

    /// <summary>
    /// Processes output data for playback.
    /// </summary>
    /// <param name="args">The audio data event arguments.</param>
    private void ProcessOutput(AudioDataEventArgs args)
    {
        if (_outputBufferQueue.TryDequeue(out float[]? outputData))
        {
            Debug.Assert(outputData.Length == _framesPerBuffer * (int)_outOptions.Channels,
                "Output buffer size mismatch - every buffer must be FramesPerBuffer * Channels");

            int copyLength = Math.Min(outputData.Length, args.SampleCount);
            Array.Copy(outputData, args.Buffer, copyLength);

            if (copyLength < args.SampleCount)
            {
                Array.Clear(args.Buffer, copyLength, args.SampleCount - copyLength);
            }
            _outputBufferPool.Add(outputData);
        }
        else
        {
            Array.Clear(args.Buffer, 0, args.SampleCount);
        }
    }

    /// <summary>
    /// Processes input data from recording.
    /// </summary>
    /// <param name="args">The audio data event arguments.</param>
    private void ProcessInput(AudioDataEventArgs args)
    {
        int actualSampleCount = args.SampleCount;
        int requiredSize = _framesPerBuffer * (int)_inOptions.Channels;

        float[]? inputData;

        if (!_inputBufferPool.TryTake(out inputData) || inputData.Length != actualSampleCount)
        {
            inputData = new float[actualSampleCount]; 
        }

        Array.Copy(args.Buffer, inputData, actualSampleCount);

        while (_inputBufferQueue.Count >= _maxQueueSize)
        {
            if (_inputBufferQueue.TryDequeue(out float[]? dummy))
                _inputBufferPool.Add(dummy);
            else
                break;
        }

        _inputBufferQueue.Enqueue(inputData);
    }

    /// <summary>
    /// Initializes the buffer pools for input and output data.
    /// </summary>
    private void InitializeBufferPools()
    {
        int outputBufferSize = _framesPerBuffer * (int)_outOptions.Channels;

        int inputBufferSize = _framesPerBuffer * (int)_inOptions.Channels * 4; 

        Debug.WriteLine($"Initializing buffer pools:");
        Debug.WriteLine($"  Output buffers: {outputBufferSize} samples");
        Debug.WriteLine($"  Input buffer pool size: {inputBufferSize} samples (dynamic sizing)");

        for (int i = 0; i < _maxQueueSize * 2; i++)
        {
            if (_outOptions.Channels > 0)
                _outputBufferPool.Add(new float[outputBufferSize]);
        }
    }

    /// <summary>
    /// Gets or sets the number of frames processed per buffer.
    /// </summary>
    public int FramesPerBuffer => _framesPerBuffer;

    /// <summary>
    /// Gets the primary engine handle (playback if available, otherwise capture).
    /// </summary>
    /// <returns>IntPtr representing the primary MiniAudio engine</returns>
    public IntPtr GetStream()
    {
        if (_playbackEngine != null)
        {
            if (!_playbackEngineHandle.HasValue || !_playbackEngineHandle.Value.IsAllocated)
                _playbackEngineHandle = GCHandle.Alloc(_playbackEngine);

            return GCHandle.ToIntPtr(_playbackEngineHandle.Value);
        }
        else if (_captureEngine != null)
        {
            if (!_captureEngineHandle.HasValue || !_captureEngineHandle.Value.IsAllocated)
                _captureEngineHandle = GCHandle.Alloc(_captureEngine);

            return GCHandle.ToIntPtr(_captureEngineHandle.Value);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Checks the active state of the audio engines.
    /// </summary>
    /// <returns>
    /// 1 - at least one engine is running
    /// 0 - no engines are running
    /// </returns>
    public int OwnAudioEngineActivate()
    {
        bool playbackRunning = _playbackEngine?.IsRunning() ?? false;
        bool captureRunning = _captureEngine?.IsRunning() ?? false;

        return (playbackRunning || captureRunning) ? 1 : 0;
    }

    /// <summary>
    /// Checks the stopped state of the audio engines.
    /// </summary>
    /// <returns>
    /// 1 - all engines are stopped
    /// 0 - at least one engine is running
    /// </returns>
    public int OwnAudioEngineStopped()
    {
        bool playbackRunning = _playbackEngine?.IsRunning() ?? false;
        bool captureRunning = _captureEngine?.IsRunning() ?? false;

        return (playbackRunning || captureRunning) ? 0 : 1;
    }

    /// <summary>
    /// Starts the audio engines.
    /// </summary>
    /// <returns>0 for success, -1 for error</returns>
    public int Start()
    {
        try
        {
            while (_outputBufferQueue.TryDequeue(out float[]? buffer))
                _outputBufferPool.Add(buffer);

            while (_inputBufferQueue.TryDequeue(out float[]? buffer))
                _inputBufferPool.Add(buffer);

            if (_playbackEngine != null)
                _playbackEngine.Start();

            if (_captureEngine != null)
                _captureEngine.Start();

            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Miniaudio start error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Stops the audio engines.
    /// </summary>
    /// <returns>0 for success</returns>
    public int Stop()
    {
        try
        {
            _playbackEngine?.Stop();

            _captureEngine?.Stop();

            while (_outputBufferQueue.TryDequeue(out float[]? buffer))
                _outputBufferPool.Add(buffer);

            while (_inputBufferQueue.TryDequeue(out float[]? buffer))
                _inputBufferPool.Add(buffer);

            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping engines: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Sends audio data to the output device.
    /// </summary>
    /// <param name="samples">A span containing the audio samples to be played.</param>
    public void Send(Span<float> samples)
    {
        if (_playbackEngine == null)
        {
            Debug.WriteLine("Warning: Trying to send audio data but no playback engine is available.");
            return;
        }

        int expectedSize = _framesPerBuffer * (int)_outOptions.Channels;
        if (samples.Length != expectedSize)
        {
            Debug.WriteLine($"Warning: Incoming sample size ({samples.Length}) doesn't match expected size ({expectedSize}). Dropping samples.");
            return;
        }

        if (_outputBufferQueue.Count >= _maxQueueSize)
            Thread.Sleep(5);

        if (_outputBufferQueue.Count < _maxQueueSize)
            EnqueueOutputBuffer(samples);
        else
            Debug.WriteLine("Output buffer queue still full after wait. Dropping samples.");
        
        _stopwatch.Restart();
    }

    /// <summary>
    /// Helper function for enqueueing samples.
    /// </summary>
    /// <param name="samples">The samples to be enqueued.</param>
    private void EnqueueOutputBuffer(Span<float> samples)
    {
        float[]? buffer;
        int expectedSize = _framesPerBuffer * (int)_outOptions.Channels;

        if (!_outputBufferPool.TryTake(out buffer))
        {
            buffer = new float[expectedSize];
        }
        else
        {
            Debug.Assert(buffer.Length == expectedSize, "EnqueueOutputBuffer: Buffer from pool has incorrect size!");
            if (buffer.Length != expectedSize)
            {
                Debug.WriteLine($"EnqueueOutputBuffer: Incorrect buffer size {buffer.Length} from pool, expected {expectedSize}. Reallocating.");
                buffer = new float[expectedSize];
            }
        }

        samples.CopyTo(buffer);
        _outputBufferQueue.Enqueue(buffer);
    }

    /// <summary>
    /// Receives audio data from the input device.
    /// </summary>
    /// <param name="samples">An output array that will be filled with the received audio samples.</param>
    public void Receives(out float[] samples)
    {
#nullable disable
        if (_inputBufferQueue.TryDequeue(out samples))
            return;

        int waitCount = 0;
        const int maxWait = 5;

        while (!_inputBufferQueue.TryDequeue(out samples) && waitCount < maxWait)
        {
            Thread.Sleep(1);
            waitCount++;
        }

        if (samples == null)
        {
            int expectedSize = _framesPerBuffer * (int)_inOptions.Channels;
            samples = new float[expectedSize];
            Array.Clear(samples, 0, samples.Length);
            Debug.WriteLine($"No input data available, returning silence ({expectedSize} samples)");
        }
#nullable restore
    }

    /// <summary>
    /// Switches to another audio device.
    /// </summary>
    /// <param name="deviceName">The name of the target device.</param>
    /// <param name="isInputDevice">True if input device; false if output device.</param>
    /// <returns>True if the switch was successful; otherwise, false.</returns>
    public bool SwitchToDevice(string deviceName, bool isInputDevice = false)
    {
        try
        {
            var engine = isInputDevice ? _captureEngine : _playbackEngine;

            if (engine == null)
            {
                Debug.WriteLine($"Warning: No {(isInputDevice ? "capture" : "playback")} engine available for device switching.");
                return false;
            }

            var devices = isInputDevice
                ? engine.CaptureDevices
                : engine.PlaybackDevices;

            var targetDevice = devices.FirstOrDefault(d =>
                d.Name?.Contains(deviceName, StringComparison.OrdinalIgnoreCase) == true);

            if (targetDevice != null)
            {
                engine.SwitchDevice(targetDevice,
                    isInputDevice ? EngineDeviceType.Capture : EngineDeviceType.Playback);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error switching device: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the list of available playback devices.
    /// </summary>
    /// <returns>Enumerable of available playback devices</returns>
    public IEnumerable<object> GetPlaybackDevices()
    {
        return _playbackEngine?.PlaybackDevices ?? Enumerable.Empty<object>();
    }

    /// <summary>
    /// Gets the list of available capture devices.
    /// </summary>
    /// <returns>Enumerable of available capture devices</returns>
    public IEnumerable<object> GetCaptureDevices()
    {
        return _captureEngine?.CaptureDevices ?? Enumerable.Empty<object>();
    }

    /// <summary>
    /// Releases all resources used by the audio engines.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_playbackEngine != null)
        {
            try
            {
                _playbackEngine.Stop();
                _playbackEngine.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error disposing playback engine: {ex.Message}");
            }
        }

        if (_captureEngine != null)
        {
            try
            {
                _captureEngine.Stop();
                _captureEngine.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error disposing capture engine: {ex.Message}");
            }
        }

        float[]? buffer;
        while (_outputBufferQueue.TryDequeue(out buffer))
            _outputBufferPool.Add(buffer);

        while (_inputBufferQueue.TryDequeue(out buffer))
            _inputBufferPool.Add(buffer);

        while (_inputBufferPool.TryTake(out _)) { }
        while (_outputBufferPool.TryTake(out _)) { }

        if (_playbackEngineHandle.HasValue && _playbackEngineHandle.Value.IsAllocated)
        {
            _playbackEngineHandle.Value.Free();
            _playbackEngineHandle = null;
        }

        if (_captureEngineHandle.HasValue && _captureEngineHandle.Value.IsAllocated)
        {
            _captureEngineHandle.Value.Free();
            _captureEngineHandle = null;
        }
    }
}
