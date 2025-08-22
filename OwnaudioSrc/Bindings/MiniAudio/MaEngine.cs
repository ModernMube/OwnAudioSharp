using Ownaudio.Bindings.Miniaudio;
using Ownaudio.Exceptions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Ownaudio.Bindings.Miniaudio.MaBinding;

namespace Ownaudio.MiniAudio
{
    /// <summary>
    /// Miniaudio-based audio engine for audio playback and capture operations.
    /// Provides a high-level interface for audio device management and real-time audio processing.
    /// </summary>
    public sealed unsafe class MiniAudioEngine : IDisposable
    {
        #region Local fields
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly MaFormat _sampleFormat;
        private readonly MaDeviceType _deviceType;
        private readonly int _sizeInFrame;

        private MaBinding.MaDataCallback? _audioCallback;
        private IntPtr _context;
        private IntPtr _device = IntPtr.Zero;
        private readonly object _syncLock = new();
        private readonly List<DeviceInfo> _playbackDevices = new();
        private readonly List<DeviceInfo> _captureDevices = new();
        private bool _isDisposed;

        private readonly ArrayPool<float> _arrayPool = ArrayPool<float>.Shared;
        private float[]? _outputBuffer;
        private float[]? _inputBuffer;
        private AudioDataEventArgs? _outputEventArgs;
        private AudioDataEventArgs? _inputEventArgs;
        private int _lastOutputBufferSize;
        private int _lastInputBufferSize;

        private const int BUFFER_POOL_SIZE = 4;
        private readonly Stack<float[]> _outputBufferPool = new();
        private readonly Stack<float[]> _inputBufferPool = new();
        private readonly Stack<AudioDataEventArgs> _outputEventArgsPool = new();
        private readonly Stack<AudioDataEventArgs> _inputEventArgsPool = new();
        #endregion

        #region Public properties
        /// <summary>
        /// Gets the sample rate used by the audio engine in Hz.
        /// </summary>
        /// <value>The sample rate in Hz (e.g., 44100, 48000).</value>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Gets the number of audio channels used by the audio engine.
        /// </summary>
        /// <value>The number of channels (e.g., 1 for mono, 2 for stereo).</value>
        public int Channels => _channels;

        /// <summary>
        /// Gets the sample format used by the audio engine.
        /// </summary>
        /// <value>The internal miniaudio sample format.</value>
        internal MaFormat SampleFormat => _sampleFormat;

        /// <summary>
        /// Gets a read-only list of available playback devices.
        /// </summary>
        /// <value>A read-only collection of playback device information.</value>
        public IReadOnlyList<DeviceInfo> PlaybackDevices => _playbackDevices;

        /// <summary>
        /// Gets a read-only list of available capture devices.
        /// </summary>
        /// <value>A read-only collection of capture device information.</value>
        public IReadOnlyList<DeviceInfo> CaptureDevices => _captureDevices;

        /// <summary>
        /// Gets the currently active playback device.
        /// </summary>
        /// <value>The current playback device information, or null if no device is active.</value>
        public DeviceInfo? CurrentPlaybackDevice { get; private set; }

        /// <summary>
        /// Gets the currently active capture device.
        /// </summary>
        /// <value>The current capture device information, or null if no device is active.</value>
        public DeviceInfo? CurrentCaptureDevice { get; private set; }

        /// <summary>
        /// Occurs when audio data is being processed.
        /// Subscribe to this event to process or generate audio data in real-time.
        /// </summary>
        public event EventHandler<AudioDataEventArgs>? AudioProcessing;
        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="MiniAudioEngine"/> class with default settings.
        /// Creates an audio context without initializing any device.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the audio context initialization fails.</exception>
        public MiniAudioEngine()
        {
            _context = MaBinding.allocate_context();

            MaResult result;
            if (OperatingSystem.IsLinux())
            {
                var backends = new[] { MaBackend.Alsa, MaBackend.PulseAudio, MaBackend.Jack };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _context);
            }
            else if (OperatingSystem.IsWindows())
            {
                var backends = new[] { MaBackend.Wasapi };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _context);
            }
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                var backends = new[] { MaBackend.CoreAudio };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _context);
            }
            else if (OperatingSystem.IsAndroid())
            {
                var backends = new[] { MaBackend.Aaudio, MaBackend.OpenSL };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _context);
            }
            else
            {
                result = MaBinding.ma_context_init(null, 0, IntPtr.Zero, _context);
            }

            MiniaudioException.ThrowIfError(result, $"Failed to initialize the miniaudio context. Error: {result}");
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MiniAudioEngine"/> class with specified audio parameters.
        /// </summary>
        /// <param name="sampleRate">The sample rate in Hz (e.g., 44100, 48000).</param>
        /// <param name="deviceType">The type of audio device to initialize (playback, capture, or duplex).</param>
        /// <param name="sampleFormat">The sample format for audio data (e.g., F32 for 32-bit float).</param>
        /// <param name="channels">The number of audio channels (1 for mono, 2 for stereo).</param>
        /// <param name="sizeInFrame">The period size in frames (buffer size for audio callbacks).</param>
        /// <exception cref="InvalidOperationException">Thrown when device initialization fails.</exception>
        public MiniAudioEngine(int sampleRate = 44100, EngineDeviceType deviceType = EngineDeviceType.Playback,
            EngineAudioFormat sampleFormat = EngineAudioFormat.F32, int channels = 2, int sizeInFrame = 512)
        {
            _sampleRate = sampleRate;
            _deviceType = (MaDeviceType)deviceType;
            _sampleFormat = (MaFormat)sampleFormat;
            _channels = channels;
            _sizeInFrame = sizeInFrame;

            InitializeBufferPools();

            InitializeAudioDevice();
        }

        /// <summary>
        /// Initializes the audio device context and default device based on the operating system.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if device initialization fails.</exception>
        private void InitializeAudioDevice()
        {
            lock (_syncLock)
            {
                _context = MaBinding.allocate_context();
                MaResult result;
                if (OperatingSystem.IsLinux())
                {
                    var backends = new[] { MaBackend.Alsa, MaBackend.PulseAudio, MaBackend.Jack };
                    result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _context);
                }
                else if (OperatingSystem.IsWindows())
                {
                    var backends = new[] { MaBackend.Wasapi };
                    result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _context);
                }
                else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    var contextConfig = new MaContextConfig
                    {
                        coreaudio_sessionCategory = false,
                        coreaudio_sessionCategoryOptions = false,
                        coreaudio_noAudioSessionActivate = true,
                        coreaudio_noAudioSessionDeactivate = true,
                        threadPrioritiesEnabled = false,
                        threadPriority = 0
                    };

                    var backends = new[] { MaBackend.CoreAudio };
                    result = MaBinding.ma_context_init(backends, (uint)backends.Length,
                        Marshal.AllocHGlobal(Marshal.SizeOf<MaContextConfig>()), _context);
                }
                else if (OperatingSystem.IsAndroid())
                {
                    var backends = new[] { MaBackend.Aaudio, MaBackend.OpenSL };
                    result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _context);
                }
                else
                {
                    result = MaBinding.ma_context_init(null, 0, IntPtr.Zero, _context);
                }

                MiniaudioException.ThrowIfError(result, $"Failed to initialize the miniaudio context. Error: {result}");

                InitializeDeviceInternal(IntPtr.Zero, _deviceType);
            }
        }

        /// <summary>
        /// Initializes a specific audio device with the given configuration.
        /// </summary>
        /// <param name="deviceId">The device ID pointer, or <see cref="IntPtr.Zero"/> for the default device.</param>
        /// <param name="type">The type of the device (playback, capture, duplex, or loopback).</param>
        /// <exception cref="InvalidOperationException">Thrown if device initialization or start fails.</exception>
        private void InitializeDeviceInternal(IntPtr deviceId, MaDeviceType type)
        {
            lock (_syncLock)
            {
                if (_device != IntPtr.Zero)
                    CleanupCurrentDevice();

                var deviceConfig = MaBinding.allocate_device_config(
                    type,
                    _sampleFormat,
                    (uint)_channels,
                    (uint)_sampleRate,
                    _audioCallback = _audioCallback ?? new MaBinding.MaDataCallback(AudioCallback),
                    type == MaDeviceType.Playback || type == MaDeviceType.Duplex ? deviceId : IntPtr.Zero,
                    type == MaDeviceType.Capture || type == MaDeviceType.Duplex ? deviceId : IntPtr.Zero,
                    (uint)_sizeInFrame);

                _device = MaBinding.allocate_device();
                var result = MaBinding.ma_device_init(_context, deviceConfig, _device);

                if (result != MaResult.Success)
                {
                    MaBinding.ma_free(_device, IntPtr.Zero, "MaEngine device config not success...");
                    _device = IntPtr.Zero;
                    throw new InvalidOperationException($"Failed to initialize the miniaudio device. Error: {result}");
                }

                result = MaBinding.ma_device_start(_device);
                if (result != MaResult.Success)
                {
                    CleanupCurrentDevice();
                    throw new InvalidOperationException($"Failed to start the miniaudio device. Error: {result}");
                }

                UpdateDevicesInfo();
                if (deviceId == IntPtr.Zero)
                {
                    CurrentPlaybackDevice = _playbackDevices.FirstOrDefault(x => x.IsDefault);
                    CurrentCaptureDevice = _captureDevices.FirstOrDefault(x => x.IsDefault);
                }
                else
                {
                    CurrentPlaybackDevice = _playbackDevices.FirstOrDefault(x => x.Id == deviceId);
                    CurrentCaptureDevice = _captureDevices.FirstOrDefault(x => x.Id == deviceId);
                }
            }
        }

        /// <summary>
        /// Cleans up and releases resources associated with the current audio device.
        /// Stops the device if running and frees allocated memory.
        /// </summary>
        private void CleanupCurrentDevice()
        {
            lock (_syncLock)
            {
                if (_device == IntPtr.Zero)
                    return;

                MaBinding.ma_device_stop(_device);
                MaBinding.ma_device_uninit(_device);
                MaBinding.ma_free(_device, IntPtr.Zero, "MaEngine clean up device...");
                _device = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Initializes buffer pools for efficient memory management during audio processing.
        /// Pre-allocates buffers based on device type to minimize garbage collection pressure.
        /// </summary>
        private void InitializeBufferPools()
        {
            // Calculate optimal buffer size
            int bufferSize = _sizeInFrame * _channels * 2; // 2x size for safety

            // Pre-allocate output buffers and event args
            if (_deviceType == MaDeviceType.Playback || _deviceType == MaDeviceType.Duplex)
            {
                for (int i = 0; i < BUFFER_POOL_SIZE; i++)
                {
                    var buffer = new float[bufferSize];
                    _outputBufferPool.Push(buffer);
                    _outputEventArgsPool.Push(new AudioDataEventArgs(buffer, AudioDataDirection.Output, 0));
                }
            }

            // Pre-allocate input buffers and event args
            if (_deviceType == MaDeviceType.Capture || _deviceType == MaDeviceType.Duplex)
            {
                for (int i = 0; i < BUFFER_POOL_SIZE; i++)
                {
                    var buffer = new float[bufferSize];
                    _inputBufferPool.Push(buffer);
                    _inputEventArgsPool.Push(new AudioDataEventArgs(buffer, AudioDataDirection.Input, 0));
                }
            }
        }

        /// <summary>
        /// Processes audio buffer using a pool-based approach to minimize allocations.
        /// </summary>
        /// <param name="pBuffer">Pointer to the native audio buffer.</param>
        /// <param name="sampleCount">Number of samples to process.</param>
        /// <param name="direction">Direction of audio data flow (input or output).</param>
        /// <param name="bufferPool">Pool of reusable audio buffers.</param>
        /// <param name="eventArgsPool">Pool of reusable event argument objects.</param>
        private unsafe void ProcessAudioBufferWithPool(
            float* pBuffer,
            int sampleCount,
            AudioDataDirection direction,
            Stack<float[]> bufferPool,
            Stack<AudioDataEventArgs> eventArgsPool)
        {
            float[] buffer = null;
            AudioDataEventArgs eventArgs = null;
            bool fromPool = false;

            // Thread-safe retrieval from pool
            lock (bufferPool)
            {
                if (bufferPool.Count > 0)
                {
                    buffer = bufferPool.Pop();
                    fromPool = true;

                    // If buffer is too small, allocate new one
                    if (buffer.Length < sampleCount)
                    {
                        buffer = new float[sampleCount];
                        fromPool = false;
                    }
                }
                else
                {
                    buffer = new float[sampleCount];
                }
            }

            lock (eventArgsPool)
            {
                if (eventArgsPool.Count > 0)
                {
                    eventArgs = eventArgsPool.Pop();
                    eventArgs.Buffer = buffer; // Update buffer reference
                    eventArgs.SampleCount = sampleCount;
                }
                else
                {
                    eventArgs = new AudioDataEventArgs(buffer, direction, sampleCount);
                }
            }

            try
            {
                if (direction == AudioDataDirection.Output)
                {
                    // Ensure clean buffer
                    Array.Clear(buffer, 0, Math.Min(buffer.Length, sampleCount));

                    // Send event
                    AudioProcessing?.Invoke(this, eventArgs);

                    // Copy data to output buffer
                    fixed (float* src = buffer)
                    {
                        Buffer.MemoryCopy(src, pBuffer, sampleCount * sizeof(float), sampleCount * sizeof(float));
                    }
                }
                else // Input
                {
                    // Copy input data
                    Buffer.MemoryCopy(pBuffer, Unsafe.AsPointer(ref buffer[0]),
                        sampleCount * sizeof(float), sampleCount * sizeof(float));

                    // Send event
                    AudioProcessing?.Invoke(this, eventArgs);
                }
            }
            finally
            {
                // Return to pool if possible
                if (fromPool)
                {
                    lock (bufferPool)
                    {
                        if (bufferPool.Count < BUFFER_POOL_SIZE)
                        {
                            bufferPool.Push(buffer);
                        }
                    }

                    lock (eventArgsPool)
                    {
                        if (eventArgsPool.Count < BUFFER_POOL_SIZE)
                        {
                            eventArgsPool.Push(eventArgs);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Audio callback function called by the miniaudio library for real-time audio processing.
        /// This method is called on the audio thread and should be optimized for low latency.
        /// </summary>
        /// <param name="pDevice">Pointer to the audio device.</param>
        /// <param name="pOutput">Pointer to the output buffer for playback data.</param>
        /// <param name="pInput">Pointer to the input buffer for capture data.</param>
        /// <param name="frameCount">Number of frames to process.</param>
        private unsafe void AudioCallback(
            IntPtr pDevice,
            void* pOutput,
            void* pInput,
            uint frameCount)
        {
            if (AudioProcessing == null)
                return;

            var sampleCount = (int)frameCount * _channels;

            if (_deviceType != MaDeviceType.Capture && pOutput != null)
            {
                ProcessAudioBufferWithPool(
                    (float*)pOutput,
                    sampleCount,
                    AudioDataDirection.Output,
                    _outputBufferPool,
                    _outputEventArgsPool);
            }

            if (_deviceType != MaDeviceType.Playback && pInput != null)
            {
                ProcessAudioBufferWithPool(
                    (float*)pInput,
                    sampleCount,
                    AudioDataDirection.Input,
                    _inputBufferPool,
                    _inputEventArgsPool);
            }
        }

        /// <summary>
        /// Legacy helper method to process audio buffers while minimizing allocations.
        /// This method is kept for backward compatibility but uses less efficient pooling.
        /// </summary>
        /// <param name="pBuffer">Pointer to the audio buffer.</param>
        /// <param name="sampleCount">Number of samples to process.</param>
        /// <param name="direction">Direction of audio data flow.</param>
        /// <param name="buffer">Reference to the buffer array.</param>
        /// <param name="eventArgs">Reference to the event arguments.</param>
        /// <param name="lastBufferSize">Reference to the last buffer size for optimization.</param>
        private unsafe void ProcessAudioBuffer(
            void* pBuffer,
            int sampleCount,
            AudioDataDirection direction,
            ref float[]? buffer,
            ref AudioDataEventArgs? eventArgs,
            ref int lastBufferSize
        )
        {
            bool needNewBuffer = buffer == null ||
                                sampleCount > buffer.Length ||
                                sampleCount < lastBufferSize / 2;

            if (needNewBuffer)
            {
                if (buffer != null)
                    _arrayPool.Return(buffer);

                int bufferSize = (int)(sampleCount * 1.2);
                buffer = _arrayPool.Rent(bufferSize);
                eventArgs = new AudioDataEventArgs(buffer, direction, sampleCount);
                lastBufferSize = sampleCount;
            }
            else if (eventArgs != null)
            {
                eventArgs.SampleCount = sampleCount;
            }

            if (direction == AudioDataDirection.Output)
            {
                AudioProcessing?.Invoke(this, eventArgs!);
                eventArgs!.Buffer.AsSpan(0, sampleCount).CopyTo(new Span<float>(pBuffer, sampleCount));
            }
            else if (direction == AudioDataDirection.Input)
            {
                new Span<float>(pBuffer, sampleCount).CopyTo(buffer.AsSpan(0, sampleCount));
                AudioProcessing?.Invoke(this, eventArgs!);
            }
        }

        /// <summary>
        /// Updates the lists of available audio devices by querying the audio context.
        /// Refreshes both playback and capture device information.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if device enumeration fails.</exception>
        public void UpdateDevicesInfo()
        {
            lock (_syncLock)
            {
                unsafe
                {
                    IntPtr pPlaybackDevices;
                    IntPtr pCaptureDevices;
                    int playbackDeviceCount;
                    int captureDeviceCount;

                    var result = MaBinding.ma_context_get_devices(
                        _context,
                        out pPlaybackDevices,
                        out pCaptureDevices,
                        out playbackDeviceCount,
                        out captureDeviceCount);

                    if (result != MaResult.Success)
                        throw new InvalidOperationException($"Failed to query miniaudio devices. Error: {result}");

                    if (pPlaybackDevices == IntPtr.Zero || pCaptureDevices == IntPtr.Zero ||
                        playbackDeviceCount == 0 || captureDeviceCount == 0)
                        throw new InvalidOperationException("Failed to query miniaudio devices.");

                    _playbackDevices.Clear();
                    for (int i = 0; i < playbackDeviceCount; i++)
                    {
                        IntPtr deviceInfoPtr = IntPtr.Add(pPlaybackDevices, i * Marshal.SizeOf<MaBinding.MaDeviceInfo>());

                        MaBinding.MaDeviceInfo nativeDeviceInfo = Marshal.PtrToStructure<MaBinding.MaDeviceInfo>(deviceInfoPtr);

                        string deviceName = nativeDeviceInfo.Name;
                        bool isDefault = nativeDeviceInfo.IsDefault;

                        Debug.WriteLine($"Playback Device {i + 1}: {deviceName} (Default: {isDefault})");

                        _playbackDevices.Add(new DeviceInfo
                        {
                            Id = deviceInfoPtr,
                            Name = deviceName,
                            IsDefault = isDefault
                        });
                    }

                    _captureDevices.Clear();
                    for (int i = 0; i < captureDeviceCount; i++)
                    {
                        IntPtr deviceInfoPtr = IntPtr.Add(pCaptureDevices, i * Marshal.SizeOf<MaBinding.MaDeviceInfo>());
                        MaBinding.MaDeviceInfo nativeDeviceInfo = Marshal.PtrToStructure<MaBinding.MaDeviceInfo>(deviceInfoPtr);

                        string deviceName = nativeDeviceInfo.Name;
                        bool isDefault = nativeDeviceInfo.IsDefault;

                        Debug.WriteLine($"Capture Device {i + 1}: {deviceName} (Default: {isDefault})");

                        _captureDevices.Add(new DeviceInfo
                        {
                            Id = deviceInfoPtr,
                            Name = deviceName,
                            IsDefault = isDefault
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Switches to a specified audio device.
        /// </summary>
        /// <param name="deviceInfo">Information about the device to switch to.</param>
        /// <param name="type">Type of the device (playback, capture, or duplex).</param>
        /// <exception cref="InvalidOperationException">Thrown if the device ID is invalid or switching fails.</exception>
        public void SwitchDevice(DeviceInfo deviceInfo, EngineDeviceType type = EngineDeviceType.Playback)
        {
            if (deviceInfo.Id == IntPtr.Zero)
                throw new InvalidOperationException("Failed to switch device. Invalid device ID.");

            InitializeDeviceInternal(deviceInfo.Id, (MaDeviceType)type);
        }

        /// <summary>
        /// Creates a new audio decoder for the specified stream with the current engine's audio parameters.
        /// </summary>
        /// <param name="stream">The audio stream to decode.</param>
        /// <returns>A new <see cref="MiniAudioDecoder"/> instance configured with the engine's parameters.</returns>
        public MiniAudioDecoder CreateDecoder(Stream stream)
        {
            return new MiniAudioDecoder(stream, (EngineAudioFormat)_sampleFormat, _channels, _sampleRate);
        }

        /// <summary>
        /// Starts audio processing on the current device.
        /// Audio callbacks will begin receiving data after this call.
        /// </summary>
        public void Start()
        {
            if (_device != IntPtr.Zero)
                MaBinding.ma_device_start(_device);
        }

        /// <summary>
        /// Stops audio processing on the current device.
        /// Audio callbacks will stop receiving data after this call.
        /// </summary>
        public void Stop()
        {
            if (_device != IntPtr.Zero)
                MaBinding.ma_device_stop(_device);
        }

        /// <summary>
        /// Checks if the audio engine is currently running.
        /// </summary>
        /// <returns><c>true</c> if the audio engine is running; otherwise, <c>false</c>.</returns>
        public bool IsRunning()
        {
            return _device != IntPtr.Zero && MaBinding.ma_device_is_started(_device);
        }

        /// <summary>
        /// Releases all resources used by the audio engine.
        /// </summary>
        public void Dispose()
        {
            lock (_syncLock)
            {
                if (_isDisposed)
                    return;

                // Clear pools
                _outputBufferPool.Clear();
                _inputBufferPool.Clear();
                _outputEventArgsPool.Clear();
                _inputEventArgsPool.Clear();

                // Release original buffers (if any)
                if (_outputBuffer != null)
                {
                    _arrayPool.Return(_outputBuffer);
                    _outputBuffer = null;
                }

                if (_inputBuffer != null)
                {
                    _arrayPool.Return(_inputBuffer);
                    _inputBuffer = null;
                }

                CleanupCurrentDevice();
                MaBinding.ma_context_uninit(_context);
                MaBinding.ma_free(_context, IntPtr.Zero, "MaEngine dispose...");

                _isDisposed = true;
            }
        }
    }

    /// <summary>
    /// Stores information about an audio device.
    /// </summary>
    public class DeviceInfo
    {
        /// <summary>
        /// Gets or sets the device identifier used internally by the audio system.
        /// </summary>
        /// <value>A pointer to the device information structure.</value>
        public IntPtr Id { get; internal set; }

        /// <summary>
        /// Gets or sets the human-readable name of the device.
        /// </summary>
        /// <value>The device name as displayed in the system.</value>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether this is the system's default device.
        /// </summary>
        /// <value><c>true</c> if this is the default device; otherwise, <c>false</c>.</value>
        public bool IsDefault { get; internal set; }
    }

    /// <summary>
    /// Specifies the direction of audio data flow.
    /// </summary>
    public enum AudioDataDirection
    {
        /// <summary>
        /// Output (playback) data flowing from the application to the audio device.
        /// </summary>
        Output,

        /// <summary>
        /// Input (recording) data flowing from the audio device to the application.
        /// </summary>
        Input
    }

    /// <summary>
    /// Provides data for audio processing events.
    /// </summary>
    public class AudioDataEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the audio buffer containing the samples.
        /// For output, fill this buffer with audio data. For input, read audio data from this buffer.
        /// </summary>
        /// <value>An array of floating-point samples.</value>
        public float[] Buffer { get; internal set; }

        /// <summary>
        /// Gets or sets the number of valid samples in the buffer.
        /// </summary>
        /// <value>The count of samples to process.</value>
        public int SampleCount { get; internal set; }

        /// <summary>
        /// Gets the direction of the audio data flow.
        /// </summary>
        /// <value>The direction indicating whether this is input or output data.</value>
        public AudioDataDirection Direction { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioDataEventArgs"/> class.
        /// </summary>
        /// <param name="buffer">The audio buffer containing samples.</param>
        /// <param name="direction">The direction of audio data flow.</param>
        /// <param name="sampleCount">The number of valid samples in the buffer.</param>
        public AudioDataEventArgs(float[] buffer, AudioDataDirection direction, int sampleCount)
        {
            Buffer = buffer;
            SampleCount = sampleCount;
            Direction = direction;
        }
    }

    /// <summary>
    /// Specifies the types of audio devices.
    /// </summary>
    public enum EngineDeviceType
    {
        /// <summary>
        /// Device for audio playback only.
        /// </summary>
        Playback = 1,

        /// <summary>
        /// Device for audio capture/recording only.
        /// </summary>
        Capture = 2,

        /// <summary>
        /// Device for both playback and capture simultaneously.
        /// </summary>
        Duplex = 3,

        /// <summary>
        /// Device for loopback recording (capturing system audio output).
        /// </summary>
        Loopback = 4
    }

    /// <summary>
    /// Specifies the audio sample formats supported by the engine.
    /// </summary>
    public enum EngineAudioFormat
    {
        /// <summary>
        /// Unknown or unsupported format.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// 8-bit unsigned integer format.
        /// </summary>
        U8 = 1,

        /// <summary>
        /// 16-bit signed integer format.
        /// </summary>
        S16 = 2,

        /// <summary>
        /// 24-bit signed integer format.
        /// </summary>
        S24 = 3,

        /// <summary>
        /// 32-bit signed integer format.
        /// </summary>
        S32 = 4,

        /// <summary>
        /// 32-bit floating point format (recommended for high quality).
        /// </summary>
        F32 = 5,

        /// <summary>
        /// Total number of supported formats.
        /// </summary>
        Count
    }
}
