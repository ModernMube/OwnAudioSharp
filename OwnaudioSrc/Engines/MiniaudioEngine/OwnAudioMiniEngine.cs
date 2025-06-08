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
/// Provides audio device interaction using separate MiniAudio engines for playback and capture operations.
/// This sealed class implements the IAudioEngine interface and manages audio buffer pools for efficient data processing.
/// </summary>
/// <remarks>
/// This engine supports both input (capture) and output (playback) audio operations with configurable sample rates,
/// channels, and buffer sizes. It uses concurrent collections for thread-safe buffer management and provides
/// automatic device switching capabilities.
/// </remarks>
public sealed class OwnAudioMiniEngine : IAudioEngine
{
    #region Constants and Configuration

    /// <summary>
    /// Maximum number of buffers that can be queued before dropping audio data.
    /// </summary>
    private const int MaxQueueSize = 10;
    
    /// <summary>
    /// Maximum number of milliseconds to wait when trying to dequeue input data.
    /// </summary>
    private const int MaxInputWaitTime = 5;
    
    /// <summary>
    /// Sleep duration in milliseconds when output buffer queue is full.
    /// </summary>
    private const int OutputBufferWaitTime = 5;
    
    #endregion

    #region Private Fields

    private readonly AudioEngineOutputOptions _outputOptions;
    private readonly AudioEngineInputOptions _inputOptions;
    private MiniAudioEngine? _playbackEngine;
    private MiniAudioEngine? _captureEngine;
    private readonly int _framesPerBuffer;
    private bool _disposed;

    // Buffer management
    private readonly ConcurrentQueue<float[]> _inputBufferQueue = new();
    private readonly ConcurrentQueue<float[]> _outputBufferQueue = new();
    private readonly ConcurrentBag<float[]> _inputBufferPool = new();
    private readonly ConcurrentBag<float[]> _outputBufferPool = new();

    // Engine handles for unmanaged resources
    private GCHandle? _playbackEngineHandle;
    private GCHandle? _captureEngineHandle;

    // Performance monitoring (can be removed in release builds)
    private readonly Stopwatch _performanceStopwatch = new();

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnAudioMiniEngine"/> class with output-only configuration.
    /// </summary>
    /// <param name="outputOptions">Configuration options for audio output. If null, default options will be used.</param>
    /// <param name="framesPerBuffer">Number of frames to process per buffer cycle. Default is 512.</param>
    /// <exception cref="MiniaudioException">
    /// Thrown when MiniAudio engine initialization fails due to hardware or configuration issues.
    /// </exception>
    /// <remarks>
    /// This constructor creates an audio engine capable of playback only. For recording capabilities,
    /// use the constructor that accepts input options.
    /// </remarks>
    public OwnAudioMiniEngine(AudioEngineOutputOptions? outputOptions = default, int framesPerBuffer = 512)
        : this(null, outputOptions, framesPerBuffer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnAudioMiniEngine"/> class with full input and output configuration.
    /// </summary>
    /// <param name="inputOptions">Configuration options for audio input (recording). If null, default options will be used.</param>
    /// <param name="outputOptions">Configuration options for audio output (playback). If null, default options will be used.</param>
    /// <param name="framesPerBuffer">Number of frames to process per buffer cycle. Default is 512.</param>
    /// <exception cref="MiniaudioException">
    /// Thrown when MiniAudio engine initialization fails due to hardware or configuration issues.
    /// </exception>
    /// <remarks>
    /// This constructor creates a full-duplex audio engine capable of both recording and playback.
    /// Engines are only created for configurations where the channel count is greater than zero.
    /// </remarks>
    public OwnAudioMiniEngine(AudioEngineInputOptions? inputOptions = default,
                             AudioEngineOutputOptions? outputOptions = default,
                             int framesPerBuffer = 512)
    {
        _inputOptions = inputOptions ?? new AudioEngineInputOptions();
        _outputOptions = outputOptions ?? new AudioEngineOutputOptions();
        _framesPerBuffer = framesPerBuffer;

        InitializePlaybackEngine();
        InitializeCaptureEngine();
        InitializeBufferPools();
    }

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the number of frames processed per buffer cycle.
    /// </summary>
    /// <value>The number of frames per buffer as specified during initialization.</value>
    public int FramesPerBuffer => _framesPerBuffer;

    #endregion

    #region Initialization Methods

    /// <summary>
    /// Initializes the playback engine if output channels are configured.
    /// </summary>
    private void InitializePlaybackEngine()
    {
        if (_outputOptions.Channels <= 0) return;

        _playbackEngine = new MiniAudioEngine(
            sampleRate: _outputOptions.SampleRate,
            deviceType: EngineDeviceType.Playback,
            sampleFormat: EngineAudioFormat.F32,
            channels: (int)_outputOptions.Channels
        );

        SetupPlaybackProcessing();
    }

    /// <summary>
    /// Initializes the capture engine if input channels are configured.
    /// </summary>
    private void InitializeCaptureEngine()
    {
        if (_inputOptions.Channels <= 0) return;

        _captureEngine = new MiniAudioEngine(
            sampleRate: _inputOptions.SampleRate,
            deviceType: EngineDeviceType.Capture,
            sampleFormat: EngineAudioFormat.F32,
            channels: (int)_inputOptions.Channels
        );

        SetupCaptureProcessing();
    }

    /// <summary>
    /// Initializes buffer pools for efficient memory management during audio processing.
    /// </summary>
    /// <remarks>
    /// Pre-allocates buffers to avoid garbage collection during real-time audio processing.
    /// Output buffers are sized according to frames per buffer, while input buffers use dynamic sizing.
    /// </remarks>
    private void InitializeBufferPools()
    {
        int outputBufferSize = _framesPerBuffer * (int)_outputOptions.Channels;
        // Input buffer size is dynamic, but we can pre-allocate a few common sizes if needed.
        // For now, we rely on GetOrCreateInputBuffer to handle allocation/pooling.

        LogBufferPoolInitialization(outputBufferSize);

        // Pre-allocate output buffers
        if (_outputOptions.Channels > 0)
        {
            for (int i = 0; i < MaxQueueSize * 2; i++) // Allocate more than max queue size to reduce initial allocations
            {
                _outputBufferPool.Add(new float[outputBufferSize]);
            }
        }
    }

    /// <summary>
    /// Logs buffer pool initialization details for debugging purposes.
    /// </summary>
    /// <param name="outputBufferSize">Size of output buffers in samples.</param>
    private static void LogBufferPoolInitialization(int outputBufferSize)
    {
        Debug.WriteLine("Initializing audio buffer pools:");
        Debug.WriteLine($"  Output buffers: {outputBufferSize} samples");
        Debug.WriteLine($"  Input buffer pool size: dynamic (handled by GetOrCreateInputBuffer)");
    }

    #endregion

    #region Audio Processing Setup

    /// <summary>
    /// Configures the audio processing callback for the playback engine.
    /// </summary>
    private void SetupPlaybackProcessing()
    {
        if (_playbackEngine == null) return;

        _playbackEngine.AudioProcessing += OnPlaybackAudioProcessing;
    }

    /// <summary>
    /// Configures the audio processing callback for the capture engine.
    /// </summary>
    private void SetupCaptureProcessing()
    {
        if (_captureEngine == null) return;

        _captureEngine.AudioProcessing += OnCaptureAudioProcessing;
    }

    /// <summary>
    /// Handles audio processing events for playback operations.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="args">Event arguments containing audio data and processing information.</param>
    private void OnPlaybackAudioProcessing(object? sender, AudioDataEventArgs args)
    {
        if (args.Direction == AudioDataDirection.Output)
        {
            ProcessOutputData(args);
        }
    }

    /// <summary>
    /// Handles audio processing events for capture operations.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="args">Event arguments containing audio data and processing information.</param>
    private void OnCaptureAudioProcessing(object? sender, AudioDataEventArgs args)
    {
        if (args.Direction == AudioDataDirection.Input)
        {
            ProcessInputData(args);
        }
    }

    #endregion

    #region Audio Data Processing

    /// <summary>
    /// Processes audio data for playback output, retrieving queued samples and copying them to the output buffer.
    /// </summary>
    /// <param name="args">Audio data event arguments containing the output buffer and sample count.</param>
    /// <remarks>
    /// If no output data is available in the queue, the output buffer is filled with silence.
    /// Buffer size validation ensures consistent audio processing.
    /// </remarks>
    private void ProcessOutputData(AudioDataEventArgs args)
    {
        if (_outputBufferQueue.TryDequeue(out float[]? outputData))
        {
            ValidateOutputBufferSize(outputData);
            CopyOutputSamples(outputData, args);
            _outputBufferPool.Add(outputData);
        }
        else
        {
            // Fill with silence when no data is available
            //Array.Clear(args.Buffer, 0, args.SampleCount);
            FastClear(args.Buffer, args.SampleCount);
        }
    }

    /// <summary>
    /// Validates that the output buffer size matches expected dimensions.
    /// </summary>
    /// <param name="outputData">The output data buffer to validate.</param>
    private void ValidateOutputBufferSize(float[] outputData)
    {
        int expectedSize = _framesPerBuffer * (int)_outputOptions.Channels;
        Debug.Assert(outputData.Length == expectedSize,
            $"Output buffer size mismatch - expected {expectedSize}, got {outputData.Length}");
    }

    /// <summary>
    /// Copies output samples to the audio processing buffer, handling size mismatches gracefully.
    /// </summary>
    /// <param name="outputData">Source audio data to copy.</param>
    /// <param name="args">Audio processing arguments containing the destination buffer.</param>
    private static void CopyOutputSamples(float[] outputData, AudioDataEventArgs args)
    {
        int copyLength = Math.Min(outputData.Length, args.SampleCount);
        Array.Copy(outputData, args.Buffer, copyLength);

        // Fill remaining buffer with silence if source data is smaller
        if (copyLength < args.SampleCount)
        {
            Array.Clear(args.Buffer, copyLength, args.SampleCount - copyLength);
        }
    }

    /// <summary>
    /// Processes incoming audio data from capture operations, managing buffer allocation and queue limits.
    /// </summary>
    /// <param name="args">Audio data event arguments containing input samples.</param>
    /// <remarks>
    /// Implements automatic queue management to prevent memory buildup by enforcing maximum queue size.
    /// Uses buffer pooling to minimize garbage collection during real-time processing.
    /// </remarks>
    private void ProcessInputData(AudioDataEventArgs args)
    {
        float[] inputBuffer = GetOrCreateInputBuffer(args.SampleCount);
        Array.Copy(args.Buffer, inputBuffer, args.SampleCount);

        EnforceInputQueueLimit();
        _inputBufferQueue.Enqueue(inputBuffer);
    }

    /// <summary>
    /// Retrieves a buffer from the input pool or creates a new one if necessary.
    /// </summary>
    /// <param name="sampleCount">Required buffer size in samples.</param>
    /// <returns>A float array buffer of the specified size.</returns>
    private float[] GetOrCreateInputBuffer(int sampleCount)
    {
        if (_inputBufferPool.TryTake(out float[]? buffer) && buffer.Length == sampleCount)
        {
            return buffer;
        }

        return new float[sampleCount];
    }

    /// <summary>
    /// Enforces input buffer queue size limits by removing excess buffers.
    /// </summary>
    private void EnforceInputQueueLimit()
    {
        while (_inputBufferQueue.Count >= MaxQueueSize)
        {
            if (_inputBufferQueue.TryDequeue(out float[]? excessBuffer))
            {
                _inputBufferPool.Add(excessBuffer);
            }
            else
            {
                break;
            }
        }
    }

    #endregion

    #region Public Audio Operations

    /// <summary>
    /// Sends audio samples to the output device for playback.
    /// </summary>
    /// <param name="samples">Audio samples to be played back. Must match the expected buffer size.</param>
    /// <remarks>
    /// The sample count must equal FramesPerBuffer * OutputChannels. Mismatched sizes will be dropped
    /// with a warning. If the output queue is full, the method will briefly wait before attempting
    /// to enqueue the samples.
    /// </remarks>
    /// <example>
    /// <code>
    /// float[] audioSamples = new float[engine.FramesPerBuffer * 2]; // Stereo
    /// // Fill audioSamples with audio data...
    /// engine.Send(audioSamples);
    /// </code>
    /// </example>
    public void Send(Span<float> samples)
    {
        if (_playbackEngine == null) return;
        if (!ValidateSampleSize(samples)) return;

        if (!WaitForOutputQueueSpace(50)) 
        {
            Debug.WriteLine("Dropping audio samples due to queue overflow");
            return; 
        }

        WaitForOutputQueueSpace();
        
        if (_outputBufferQueue.Count < MaxQueueSize)
        {
            EnqueueOutputBuffer(samples);
        }
        else
        {
            Debug.WriteLine("Output buffer queue full after wait period. Dropping audio samples.");
        }
        
        _performanceStopwatch.Restart();
    }

    /// <summary>
    /// Validates that the incoming sample size matches the expected buffer dimensions.
    /// </summary>
    /// <param name="samples">Audio samples to validate.</param>
    /// <returns>True if the sample size is correct, false otherwise.</returns>
    private bool ValidateSampleSize(Span<float> samples)
    {
        int expectedSize = _framesPerBuffer * (int)_outputOptions.Channels;
        if (samples.Length != expectedSize)
        {
            Debug.WriteLine($"Warning: Sample size mismatch - expected {expectedSize}, got {samples.Length}. Dropping samples.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Waits briefly if the output buffer queue is at capacity.
    /// </summary>
    private bool WaitForOutputQueueSpace(int timeoutMs = 50) // Max 50ms várakozás
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (_outputBufferQueue.Count >= MaxQueueSize)
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                Debug.WriteLine($"Queue wait timeout after {timeoutMs}ms - dropping samples");
                return false; 
            }

            Thread.Sleep(1);
        }

        return true;
    }

    /// <summary>
    /// Enqueues audio samples into the output buffer queue using buffer pooling for efficiency.
    /// </summary>
    /// <param name="samples">Audio samples to enqueue.</param>
    private void EnqueueOutputBuffer(Span<float> samples)
    {
        float[] buffer = GetOrCreateOutputBuffer();
        samples.CopyTo(buffer);
        _outputBufferQueue.Enqueue(buffer);
    }

    /// <summary>
    /// Retrieves a buffer from the output pool or creates a new one if necessary.
    /// </summary>
    /// <returns>A float array buffer sized for output operations.</returns>
    private float[] GetOrCreateOutputBuffer()
    {
        int expectedSize = _framesPerBuffer * (int)_outputOptions.Channels;

        if (_outputBufferPool.TryTake(out float[]? buffer))
        {
            if (ValidatePooledBufferSize(buffer, expectedSize))
            {
                return buffer;
            }
        }

        return new float[expectedSize];
    }

    /// <summary>
    /// Validates that a pooled buffer has the correct size.
    /// </summary>
    /// <param name="buffer">Buffer to validate.</param>
    /// <param name="expectedSize">Expected buffer size.</param>
    /// <returns>True if the buffer size is correct, false otherwise.</returns>
    private static bool ValidatePooledBufferSize(float[] buffer, int expectedSize)
    {
        if (buffer.Length == expectedSize) return true;

        Debug.WriteLine($"Warning: Pooled buffer size mismatch - expected {expectedSize}, got {buffer.Length}. Creating new buffer.");
        return false;
    }

    /// <summary>
    /// Receives audio samples from the input device.
    /// </summary>
    /// <param name="samples">Output parameter that will contain the received audio samples.</param>
    /// <remarks>
    /// This method attempts to dequeue audio data from the input buffer. If no data is immediately
    /// available, it will wait briefly for incoming data. If no data becomes available within the
    /// wait period, it returns a buffer filled with silence.
    /// </remarks>
    /// <example>
    /// <code>
    /// engine.Receives(out float[] inputSamples);
    /// // Process inputSamples...
    /// </code>
    /// </example>
    public void Receives(out float[] samples)
    {
        if (TryGetInputSamples(out samples))
        {
            return;
        }

        WaitForInputData(out samples);

        if (samples == null)
        {
            samples = CreateSilentInputBuffer();
        }
    }

    /// <summary>
    /// Attempts to immediately retrieve input samples from the queue.
    /// </summary>
    /// <param name="samples">Retrieved samples, or null if none available.</param>
    /// <returns>True if samples were successfully retrieved, false otherwise.</returns>
    private bool TryGetInputSamples(out float[] samples)
    {
        return _inputBufferQueue.TryDequeue(out samples!);
    }

    /// <summary>
    /// Waits for input data to become available, with timeout.
    /// </summary>
    /// <param name="samples">Retrieved samples, or null if timeout occurred.</param>
    private void WaitForInputData(out float[] samples)
    {
        samples = null!;
        int waitCount = 0;

#nullable disable
        while (!_inputBufferQueue.TryDequeue(out samples) && waitCount < MaxInputWaitTime)
        {
            Thread.Sleep(1);
            waitCount++;
        }
#nullable restore
    }

    /// <summary>
    /// Creates a buffer filled with silence when no input data is available.
    /// </summary>
    /// <returns>A silent audio buffer of the expected input size.</returns>
    private float[] CreateSilentInputBuffer()
    {
        int expectedSize = _framesPerBuffer * (int)_inputOptions.Channels;
        float[] silentBuffer = new float[expectedSize];
        Array.Clear(silentBuffer, 0, silentBuffer.Length);
        
        Debug.WriteLine($"No input data available - returning silence ({expectedSize} samples)");
        return silentBuffer;
    }

    #endregion

    #region Engine Control Operations

    /// <summary>
    /// Gets a handle to the primary audio engine for interoperability with unmanaged code.
    /// </summary>
    /// <returns>
    /// An IntPtr representing the primary MiniAudio engine handle. Returns the playback engine
    /// if available, otherwise the capture engine. Returns IntPtr.Zero if no engines are available.
    /// </returns>
    /// <remarks>
    /// The returned handle is managed through GCHandle to prevent garbage collection of the
    /// underlying engine objects. This handle should be used carefully in unmanaged interop scenarios.
    /// </remarks>
    public IntPtr GetStream()
    {
        if (_playbackEngine != null)
        {
            return GetOrCreateEngineHandle(ref _playbackEngineHandle, _playbackEngine);
        }

        if (_captureEngine != null)
        {
            return GetOrCreateEngineHandle(ref _captureEngineHandle, _captureEngine);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Gets or creates a GC handle for the specified engine.
    /// </summary>
    /// <param name="handleField">Reference to the handle field to manage.</param>
    /// <param name="engine">Engine object to create handle for.</param>
    /// <returns>IntPtr representing the engine handle.</returns>
    private static IntPtr GetOrCreateEngineHandle(ref GCHandle? handleField, object engine)
    {
        if (!handleField.HasValue || !handleField.Value.IsAllocated)
        {
            handleField = GCHandle.Alloc(engine);
        }

        return GCHandle.ToIntPtr(handleField.Value);
    }

    /// <summary>
    /// Returns a numeric value about the activity of the audio engine
    /// </summary>
    /// <returns>
    /// 0 - the engine not playing or recording
    /// 1 - the engine plays or records
    /// negative value if there is an error
    /// </returns>
    public int OwnAudioEngineActivate()
    {
        bool isAnyEngineRunning = IsPlaybackEngineRunning() || IsCaptureEngineRunning();
        return isAnyEngineRunning ? 1 : 0;
    }

    /// <summary>
    /// It returns a value whether the motor is stopped or running
    /// </summary>
    /// <returns>
    /// 0 - the engine running
    /// 1 - the engine stopped
    /// negative value if there is an error
    /// </returns>
    public int OwnAudioEngineStopped()
    {
        bool isAnyEngineRunning = IsPlaybackEngineRunning() || IsCaptureEngineRunning();
        return isAnyEngineRunning ? 0 : 1;
    }

    /// <summary>
    /// Checks if the playback engine is currently running.
    /// </summary>
    /// <returns>True if playback engine exists and is running, false otherwise.</returns>
    private bool IsPlaybackEngineRunning() => _playbackEngine?.IsRunning() ?? false;

    /// <summary>
    /// Checks if the capture engine is currently running.
    /// </summary>
    /// <returns>True if capture engine exists and is running, false otherwise.</returns>
    private bool IsCaptureEngineRunning() => _captureEngine?.IsRunning() ?? false;

    /// <summary>
    /// Starts all configured audio engines and prepares them for audio processing.
    /// </summary>
    /// <returns>
    /// 0 for successful startup of all engines;
    /// -1 if an error occurred during startup.
    /// </returns>
    /// <remarks>
    /// Before starting the engines, this method clears any existing buffers in the queues
    /// and returns them to their respective pools. This ensures a clean state for audio processing.
    /// </remarks>
    /// <example>
    /// <code>
    /// int result = audioEngine.Start();
    /// if (result == 0)
    /// {
    ///     Console.WriteLine("Audio engines started successfully");
    /// }
    /// else
    /// {
    ///     Console.WriteLine("Failed to start audio engines");
    /// }
    /// </code>
    /// </example>
    public int Start()
    {
        try
        {
            ClearAllBufferQueues();
            StartIndividualEngines();
            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting audio engines: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Clears all buffer queues and returns buffers to their pools.
    /// </summary>
    private void ClearAllBufferQueues()
    {
        ClearBufferQueue(_outputBufferQueue, _outputBufferPool);
        ClearBufferQueue(_inputBufferQueue, _inputBufferPool);
    }

    /// <summary>
    /// Clears a specific buffer queue and returns buffers to the pool.
    /// </summary>
    /// <param name="queue">Queue to clear.</param>
    /// <param name="pool">Pool to return buffers to.</param>
    private static void ClearBufferQueue(ConcurrentQueue<float[]> queue, ConcurrentBag<float[]> pool)
    {
        while (queue.TryDequeue(out float[]? buffer))
        {
            pool.Add(buffer);
        }
    }

    /// <summary>
    /// Starts individual audio engines if they exist.
    /// </summary>
    private void StartIndividualEngines()
    {
        _playbackEngine?.Start();
        _captureEngine?.Start();
    }

    /// <summary>
    /// Stops all audio engines and cleans up their buffer queues.
    /// </summary>
    /// <returns>
    /// 0 for successful shutdown (always returns 0, as exceptions are caught and logged).
    /// </returns>
    /// <remarks>
    /// This method gracefully stops both playback and capture engines, then clears all
    /// buffer queues and returns the buffers to their respective pools for reuse.
    /// Any errors during shutdown are logged but do not prevent the method from completing.
    /// </remarks>
    /// <example>
    /// <code>
    /// int result = audioEngine.Stop();
    /// Console.WriteLine("Audio engines stopped");
    /// </code>
    /// </example>
    public int Stop()
    {
        try
        {
            StopIndividualEngines();
            ClearAllBufferQueues();
            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping audio engines: {ex.Message}");
            return 0; // Always return success for stop operations
        }
    }

    /// <summary>
    /// Stops individual audio engines if they exist.
    /// </summary>
    private void StopIndividualEngines()
    {
        _playbackEngine?.Stop();
        _captureEngine?.Stop();
    }

    #endregion

    #region Device Management

    /// <summary>
    /// Switches the audio processing to a different audio device.
    /// </summary>
    /// <param name="deviceName">
    /// The name or partial name of the target audio device. Device matching is case-insensitive
    /// and supports partial name matching.
    /// </param>
    /// <param name="isInputDevice">
    /// True to switch the input (capture) device; false to switch the output (playback) device.
    /// Default is false (output device).
    /// </param>
    /// <returns>
    /// True if the device switch was successful; false if the device was not found,
    /// the corresponding engine is not available, or an error occurred during switching.
    /// </returns>
    /// <remarks>
    /// The device switching operation searches through available devices using case-insensitive
    /// partial matching. The first device whose name contains the specified deviceName will be selected.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Switch to a USB headset for output
    /// bool success = engine.SwitchToDevice("USB Headset", false);
    /// 
    /// // Switch to built-in microphone for input
    /// bool success = engine.SwitchToDevice("Built-in Microphone", true);
    /// </code>
    /// </example>
    public bool SwitchToDevice(string deviceName, bool isInputDevice = false)
    {
        try
        {
            var engine = GetTargetEngine(isInputDevice);
            if (engine == null)
            {
                LogEngineNotAvailable(isInputDevice);
                return false;
            }

            DeviceInfo targetDevice = FindDeviceByName(engine, deviceName, isInputDevice);
            if (targetDevice == null)
            {
                return false;
            }

            var deviceType = isInputDevice ? EngineDeviceType.Capture : EngineDeviceType.Playback;
            engine.SwitchDevice(targetDevice, deviceType);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error switching to device '{deviceName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the appropriate engine for device switching operations.
    /// </summary>
    /// <param name="isInputDevice">True for input engine, false for output engine.</param>
    /// <returns>The target engine, or null if not available.</returns>
    private MiniAudioEngine? GetTargetEngine(bool isInputDevice)
    {
        return isInputDevice ? _captureEngine : _playbackEngine;
    }

    /// <summary>
    /// Logs a message when the requested engine type is not available.
    /// </summary>
    /// <param name="isInputDevice">True if input engine was requested, false for output engine.</param>
    private static void LogEngineNotAvailable(bool isInputDevice)
    {
        string engineType = isInputDevice ? "capture" : "playback";
        Debug.WriteLine($"Warning: No {engineType} engine available for device switching.");
    }

    /// <summary>
    /// Finds a device by name using partial, case-insensitive matching.
    /// </summary>
    /// <param name="engine">Engine to search for devices.</param>
    /// <param name="deviceName">Device name to search for.</param>
    /// <param name="isInputDevice">True for input devices, false for output devices.</param>
    /// <returns>The found device, or null if not found.</returns>
    private static DeviceInfo? FindDeviceByName(MiniAudioEngine engine, string deviceName, bool isInputDevice)
    {
        var devices = isInputDevice ? engine.CaptureDevices : engine.PlaybackDevices;

        return devices.FirstOrDefault(device =>
        {
            // Assuming devices have a Name property - adjust based on actual device object structure
            var deviceNameProperty = device.GetType().GetProperty("Name");
            var name = deviceNameProperty?.GetValue(device) as string;
            return name?.Contains(deviceName, StringComparison.OrdinalIgnoreCase) == true;
        });
    }

    /// <summary>
    /// Gets the list of available audio playback devices.
    /// </summary>
    /// <returns>
    /// An enumerable collection of available playback devices. Returns an empty collection
    /// if no playback engine is configured or available.
    /// </returns>
    /// <remarks>
    /// The returned objects represent audio devices and their specific type depends on the
    /// underlying MiniAudio implementation. These objects can be used for device enumeration
    /// and selection purposes.
    /// </remarks>
    /// <example>
    /// <code>
    /// var playbackDevices = engine.GetPlaybackDevices();
    /// foreach (var device in playbackDevices)
    /// {
    ///     Console.WriteLine($"Available playback device: {device}");
    /// }
    /// </code>
    /// </example>
    public IEnumerable<object> GetPlaybackDevices()
    {
        return _playbackEngine?.PlaybackDevices ?? Enumerable.Empty<object>();
    }

    /// <summary>
    /// Gets the list of available audio capture devices.
    /// </summary>
    /// <returns>
    /// An enumerable collection of available capture devices. Returns an empty collection
    /// if no capture engine is configured or available.
    /// </returns>
    /// <remarks>
    /// The returned objects represent audio input devices and their specific type depends on the
    /// underlying MiniAudio implementation. These objects can be used for device enumeration
    /// and microphone selection purposes.
    /// </remarks>
    /// <example>
    /// <code>
    /// var captureDevices = engine.GetCaptureDevices();
    /// foreach (var device in captureDevices)
    /// {
    ///     Console.WriteLine($"Available capture device: {device}");
    /// }
    /// </code>
    /// </example>
    public IEnumerable<object> GetCaptureDevices()
    {
        return _captureEngine?.CaptureDevices ?? Enumerable.Empty<object>();
    }

    #endregion

    #region Resource Management and Disposal

    /// <summary>
    /// Releases all resources used by the audio engines and cleans up unmanaged handles.
    /// </summary>
    /// <remarks>
    /// This method implements the Dispose pattern and ensures proper cleanup of:
    /// - Both playback and capture engines
    /// - All buffer queues and pools
    /// - Unmanaged GC handles used for engine interoperability
    /// 
    /// The method is safe to call multiple times and will only perform cleanup on the first call.
    /// Any errors during disposal are logged but do not prevent the method from completing.
    /// </remarks>
    /// <example>
    /// <code>
    /// using (var audioEngine = new OwnAudioMiniEngine(outputOptions))
    /// {
    ///     // Use the audio engine...
    /// } // Dispose is called automatically
    /// 
    /// // Or call explicitly:
    /// audioEngine.Dispose();
    /// </code>
    /// </example>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        DisposeEngines();
        ClearAllBuffers();
        FreeUnmanagedHandles();
    }

    /// <summary>
    /// Disposes both audio engines safely, logging any errors that occur.
    /// </summary>
    private void DisposeEngines()
    {
        DisposeEngine(_playbackEngine, "playback");
        DisposeEngine(_captureEngine, "capture");
    }

    /// <summary>
    /// Safely disposes a single audio engine.
    /// </summary>
    /// <param name="engine">Engine to dispose, or null.</param>
    /// <param name="engineType">Type description for error logging.</param>
    private static void DisposeEngine(MiniAudioEngine? engine, string engineType)
    {
        if (engine == null) return;

        try
        {
            engine.Stop();
            engine.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error disposing {engineType} engine: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears all buffer queues and pools, ensuring no memory leaks.
    /// </summary>
    private void ClearAllBuffers()
    {
        // Move queued buffers back to pools
        ClearBufferQueue(_outputBufferQueue, _outputBufferPool);
        ClearBufferQueue(_inputBufferQueue, _inputBufferPool);

        // Clear the pools completely
        ClearBufferPool(_inputBufferPool);
        ClearBufferPool(_outputBufferPool);
    }

    /// <summary>
    /// Completely clears a buffer pool.
    /// </summary>
    /// <param name="pool">Buffer pool to clear.</param>
    private static void ClearBufferPool(ConcurrentBag<float[]> pool)
    {
        while (pool.TryTake(out _))
        {
            // Continue until pool is empty
        }
    }

    /// <summary>
    /// Frees any allocated GC handles to prevent memory leaks.
    /// </summary>
    private void FreeUnmanagedHandles()
    {
        FreeGCHandle(ref _playbackEngineHandle);
        FreeGCHandle(ref _captureEngineHandle);
    }

    /// <summary>
    /// Safely frees a GC handle if it exists and is allocated.
    /// </summary>
    /// <param name="handleField">Reference to the handle field to free.</param>
    private static void FreeGCHandle(ref GCHandle? handleField)
    {
        if (handleField.HasValue && handleField.Value.IsAllocated)
        {
            handleField.Value.Free();
            handleField = null;
        }
    }

    /// <summary>
    /// Efficiently clears a float array buffer using optimized methods based on buffer size.
    /// </summary>
    /// <param name="buffer">The float array buffer to clear.</param>
    /// <param name="length">The number of elements to clear from the start of the buffer.</param>
    /// <remarks>
    /// This method uses size-based optimization:
    /// - For buffers ≤1024 elements: Uses Span.Clear() which is optimized for smaller buffers
    /// - For larger buffers: Uses Array.Clear() which is more efficient for larger memory blocks
    /// This approach provides better performance across different buffer sizes.
    /// </remarks>
    private static void FastClear(float[] buffer, int length)
    {
        if (length <= 1024)
            buffer.AsSpan(0, length).Clear(); // Span.Clear - optimized for smaller buffers
        else
            Array.Clear(buffer, 0, length); // Array.Clear - more efficient for larger buffers
    }

    #endregion
}
