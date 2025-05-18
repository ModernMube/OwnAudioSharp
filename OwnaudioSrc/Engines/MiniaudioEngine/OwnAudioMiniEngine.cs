using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Exceptions;
using Ownaudio.MiniAudio;

namespace Ownaudio.Engines;

/// <summary>
/// Interact with audio devices using MiniAudio library.
/// This class cannot be inherited.
/// <para>Implements: <see cref="IAudioEngine"/>.</para>
/// </summary>
public sealed class OwnAudioMiniEngine : IAudioEngine
{
    private readonly AudioEngineOutputOptions _outOptions;
    private readonly AudioEngineInputOptions _inOptions;
    private readonly MiniAudioEngine _miniAudioEngine;
    private readonly int _framesPerBuffer;
    private bool _disposed;

    private readonly ConcurrentQueue<float[]> _inputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly ConcurrentQueue<float[]> _outputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly ConcurrentBag<float[]> _inputBufferPool = new ConcurrentBag<float[]>();
    private readonly ConcurrentBag<float[]> _outputBufferPool = new ConcurrentBag<float[]>();
    private readonly int _maxQueueSize = 10;

    private Stopwatch _stopwatch = new Stopwatch();

    private GCHandle? _engineHandle; 

    /// <summary>
    /// Initializes a new instance of OwnAudioMiniEngine with only output options.
    /// </summary>
    /// <param name="outOptions">Optional audio engine output options.</param>
    /// <param name="framesPerBuffer">The number of frames in the buffer (default 1024).</param>
    /// <exception cref="MiniaudioException">
    /// Thrown if an error occurs during MiniAudio initialization.
    /// </exception>
    public OwnAudioMiniEngine(AudioEngineOutputOptions? outOptions = default, int framesPerBuffer = 1024)
        : this(null, outOptions, framesPerBuffer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OwnAudioMiniEngine class with both input and output options.
    /// </summary>
    /// <param name="inOptions">Optional audio engine input options.</param>
    /// <param name="outOptions">Optional audio engine output options.</param>
    /// <param name="framesPerBuffer">Number of frames per buffer (default is 1024).</param>
    /// <exception cref="MiniaudioException">
    /// Thrown when errors occur during MiniAudio initialization.
    /// </exception>
    public OwnAudioMiniEngine(AudioEngineInputOptions? inOptions = default, AudioEngineOutputOptions? outOptions = default, int framesPerBuffer = 1024)
    {
        _inOptions = inOptions ?? new AudioEngineInputOptions();
        _outOptions = outOptions ?? new AudioEngineOutputOptions();
        _framesPerBuffer = framesPerBuffer;

        var deviceType = EngineDeviceType.Playback;
        if (_inOptions.Channels > 0 && _outOptions.Channels > 0)
            deviceType = EngineDeviceType.Duplex;
        else if (_inOptions.Channels > 0)
            deviceType = EngineDeviceType.Capture;

        _miniAudioEngine = new MiniAudioEngine(
            sampleRate: (int)_outOptions.SampleRate,
            deviceType: deviceType,
            sampleFormat: EngineAudioFormat.F32,
            channels: (int)(_outOptions.Channels > 0 ? _outOptions.Channels : _inOptions.Channels)
        );

        InitializeBufferPools();
        SetupAudioProcessing();
    }

    /// <summary>
    /// Sets up the audio processing callback for MiniAudio.
    /// </summary>
    private void SetupAudioProcessing()
    {
        _miniAudioEngine.AudioProcessing += (sender, args) =>
        {
            if (args.Direction == AudioDataDirection.Output)
            {
                ProcessOutput(args);
            }
            else if (args.Direction == AudioDataDirection.Input)
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
        int requiredSize = _framesPerBuffer * (int)_inOptions.Channels;
        float[]? inputData;

        if (!_inputBufferPool.TryTake(out inputData))
        {
            inputData = new float[requiredSize];
        }
        else
        {
            Debug.Assert(inputData.Length == requiredSize, "ProcessInput: Buffer from pool has incorrect size!");

            if (inputData.Length != requiredSize)
            {
                Debug.WriteLine($"ProcessInput: Incorrect buffer size {inputData.Length} from pool, expected {requiredSize}. Reallocating.");
                inputData = new float[requiredSize];
            }
        }

        int copyLength = Math.Min(args.SampleCount, requiredSize);
        Array.Copy(args.Buffer, inputData, copyLength);

        if (copyLength < requiredSize)
        {
            Array.Clear(inputData, copyLength, requiredSize - copyLength);
        }

        while (_inputBufferQueue.Count >= _maxQueueSize)
        {
            if (_inputBufferQueue.TryDequeue(out float[]? dummy))
            {
                _inputBufferPool.Add(dummy);
            }
            else
            {
                break;
            }
        }
        _inputBufferQueue.Enqueue(inputData);
    }

    /// <summary>
    /// Initializes the buffer pools for input and output data.
    /// </summary>
    private void InitializeBufferPools()
    {
        int outputBufferSize = _framesPerBuffer * (int)_outOptions.Channels;
        int inputBufferSize = _framesPerBuffer * (int)_inOptions.Channels;

        for (int i = 0; i < _maxQueueSize * 2; i++)
        {
            if (_outOptions.Channels > 0)
                _outputBufferPool.Add(new float[outputBufferSize]);

            if (_inOptions.Channels > 0)
                _inputBufferPool.Add(new float[inputBufferSize]);
        }
    }

    /// <summary>
    /// Gets or sets the number of frames processed per buffer.
    /// </summary>
    public int FramesPerBuffer => _framesPerBuffer;

    /// <summary>
    /// Gets the MiniAudio engine internal handle.
    /// </summary>
    /// <returns>IntPtr representing the MiniAudio engine</returns>
    public IntPtr GetStream()
    {
        if (!_engineHandle.HasValue || !_engineHandle.Value.IsAllocated)
        {
            _engineHandle = GCHandle.Alloc(_miniAudioEngine);
        }
        return GCHandle.ToIntPtr(_engineHandle.Value);
    }

    /// <summary>
    /// Checks the active state of the audio engine.
    /// </summary>
    /// <returns>
    /// 1 - the engine is playing or recording
    /// 0 - the engine is not playing or recording
    /// </returns>
    public int OwnAudioEngineActivate()
    {
        return _miniAudioEngine.IsRunning() ? 1 : 0;
    }

    /// <summary>
    /// Checks the stopped state of the audio engine.
    /// </summary>
    /// <returns>
    /// 1 - the engine is stopped
    /// 0 - the engine is running
    /// </returns>
    public int OwnAudioEngineStopped()
    {
        return _miniAudioEngine.IsRunning() ? 0 : 1;
    }

    /// <summary>
    /// Starts the audio engine.
    /// </summary>
    /// <returns>0 for success</returns>
    public int Start()
    {
        try
        {
            while (_outputBufferQueue.TryDequeue(out float[]? buffer))
                _outputBufferPool.Add(buffer);

            while (_inputBufferQueue.TryDequeue(out float[]? buffer))
                _inputBufferPool.Add(buffer);

            _miniAudioEngine.Start();
            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Miniaudio initialize error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Stops the audio engine.
    /// </summary>
    /// <returns>0 for success</returns>
    public int Stop()
    {
        _miniAudioEngine.Stop();

        while (_outputBufferQueue.TryDequeue(out float[]? buffer))
            _outputBufferPool.Add(buffer);

        while (_inputBufferQueue.TryDequeue(out float[]? buffer))
            _inputBufferPool.Add(buffer);

        return 0;
    }

    /// <summary>
    /// Sends audio data to the output device.
    /// </summary>
    /// <param name="samples">A span containing the audio samples to be played.</param>
    public void Send(Span<float> samples)
    {
        int expectedSize = _framesPerBuffer * (int)_outOptions.Channels;
        if (samples.Length != expectedSize)
        {
            Debug.WriteLine($"Warning: Incoming sample size ({samples.Length}) doesn't match expected size ({expectedSize}). Dropping samples.");
            return;
        }

        if (_outputBufferQueue.Count >= _maxQueueSize)
        {
            Thread.Sleep(5);
        }

        if (_outputBufferQueue.Count < _maxQueueSize)
        {
            EnqueueOutputBuffer(samples);
        }
        else
        {
            Debug.WriteLine("Output buffer queue still full after wait. Dropping samples.");
        }
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
        {
            Debug.Assert(samples != null && samples.Length == _framesPerBuffer * (int)_inOptions.Channels,
                "Input buffer size mismatch - every buffer must be FramesPerBuffer * Channels");
            return;
        }

        int waitCount = 0;
        const int maxWait = 20;

        while (!_inputBufferQueue.TryDequeue(out samples) && waitCount < maxWait)
        {
            Thread.Sleep(5);
            waitCount++;
        }

        if (samples == null)
        {
            int requiredSize = _framesPerBuffer * (int)_inOptions.Channels;
            if (!_inputBufferPool.TryTake(out samples))
            {
                samples = new float[requiredSize];
            }
            else
            {
                Debug.Assert(samples.Length == requiredSize, "Receives: Buffer from pool has incorrect size!");
                if (samples.Length != requiredSize)
                {
                    Debug.WriteLine($"Receives: Incorrect buffer size {samples.Length} from pool for empty data, expected {requiredSize}. Reallocating.");
                    samples = new float[requiredSize];
                }
            }
            Array.Clear(samples, 0, samples.Length);
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
            var devices = isInputDevice
                ? _miniAudioEngine.CaptureDevices
                : _miniAudioEngine.PlaybackDevices;

            var targetDevice = devices.FirstOrDefault(d =>
                d.Name?.Contains(deviceName, StringComparison.OrdinalIgnoreCase) == true);

            if (targetDevice != null)
            {
                _miniAudioEngine.SwitchDevice(targetDevice,
                    isInputDevice ? EngineDeviceType.Capture : EngineDeviceType.Playback);
                return true;
            }
            return false;
        }
        catch (Exception ex) // Általánosabb hibaelfogás a robusztusság érdekében
        {
            Debug.WriteLine($"Error switching device: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Releases all resources used by the audio engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_miniAudioEngine != null)
        {
            try
            {
                _miniAudioEngine.Stop();
                _miniAudioEngine.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error disposing MiniAudio engine: {ex.Message}");
            }
        }

        float[]? buffer;
        while (_outputBufferQueue.TryDequeue(out buffer))
            _outputBufferPool.Add(buffer);

        while (_inputBufferQueue.TryDequeue(out buffer))
            _inputBufferPool.Add(buffer);

        while (_inputBufferPool.TryTake(out _)) { }
        while (_outputBufferPool.TryTake(out _)) { }

        if (_engineHandle.HasValue && _engineHandle.Value.IsAllocated)
        {
            _engineHandle.Value.Free();
            _engineHandle = null;
        }
    }
}
