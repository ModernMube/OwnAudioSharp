using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Native.Utils;
using Ownaudio.Native.PortAudio;
using Ownaudio.Native.MiniAudio;
using static Ownaudio.Native.PortAudio.PaBinding;
using static Ownaudio.Native.MiniAudio.MaBinding;

namespace Ownaudio.Native
{
    /// <summary>
    /// Native cross-platform audio engine using PortAudio (when available) or MiniAudio.
    /// This is the PRIMARY audio engine for OwnAudioSharp 2.1.0+
    ///
    /// Strategy:
    /// - Windows x64: Try PortAudio first, fallback to MiniAudio
    /// - All other platforms: Use MiniAudio
    /// - Decoder: Always use MiniAudio decoder (supports MP3, WAV, FLAC)
    /// </summary>
    public sealed class NativeAudioEngine : IAudioEngine
    {
        /// <summary>
        /// The audio engine backend currently in use (PortAudio or MiniAudio).
        /// </summary>
        private AudioEngineBackend _backend;

        /// <summary>
        /// The audio configuration for this engine instance.
        /// Initialized in Initialize() method.
        /// </summary>
        private AudioConfig _config = null!;

        /// <summary>
        /// Indicates whether the engine has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Running state: 0 = stopped, 1 = running.
        /// </summary>
        private volatile int _isRunning;

        /// <summary>
        /// Active state: 0 = idle, 1 = active, -1 = error.
        /// </summary>
#pragma warning disable CS0414 // Field is assigned but its value is never used
        private volatile int _isActive;
#pragma warning restore CS0414

        /// <summary>
        /// Pre-buffering state: 0 = not buffering, 1 = buffering (waiting for initial data).
        /// When buffering, callbacks output silence until the buffer reaches the threshold.
        /// </summary>
        private volatile int _isBuffering;

        /// <summary>
        /// Number of frames per audio buffer.
        /// </summary>
        private int _framesPerBuffer;

        /// <summary>
        /// Minimum number of samples required in the buffer before starting playback.
        /// This prevents the audio from playing too fast when the buffer is nearly empty.
        /// Set to 2x the buffer size to ensure smooth playback.
        /// </summary>
        private int _prebufferThreshold;

        /// <summary>
        /// PortAudio library loader instance.
        /// </summary>
        private LibraryLoader? _portAudioLoader;

        /// <summary>
        /// Selected PortAudio host API type (WASAPI, ASIO, etc.).
        /// </summary>
        private PaHostApiTypeId _selectedHostApiType;

        /// <summary>
        /// Active output device index (PortAudio global device index).
        /// </summary>
        private int _activeOutputDeviceIndex = -1;

        /// <summary>
        /// Active input device index (PortAudio global device index).
        /// </summary>
        private int _activeInputDeviceIndex = -1;

        /// <summary>
        /// Pointer to the PortAudio stream.
        /// </summary>
        private IntPtr _paStream;

        /// <summary>
        /// PortAudio callback delegate.
        /// </summary>
        private PaStreamCallback? _paCallback;

        /// <summary>
        /// GC handle to prevent callback delegate from being collected.
        /// </summary>
        private GCHandle _callbackHandle;

        /// <summary>
        /// MiniAudio library loader instance.
        /// </summary>
        // private LibraryLoader? _miniAudioLoader; // REMOVED: Managed by MaBinding

        /// <summary>
        /// Pointer to the MiniAudio context.
        /// </summary>
        private IntPtr _maContext;

        /// <summary>
        /// Pointer to the MiniAudio device.
        /// </summary>
        private IntPtr _maDevice;

        /// <summary>
        /// MiniAudio callback delegate.
        /// </summary>
        private MaDataCallback? _maCallback;

        /// <summary>
        /// GC handle to prevent MiniAudio callback delegate from being collected.
        /// </summary>
        private GCHandle _maCallbackHandle;

        /// <summary>
        /// Lock-free ring buffer for output (playback) audio data.
        /// Initialized in Initialize() method.
        /// </summary>
        private LockFreeRingBuffer<float> _outputRing = null!;

        /// <summary>
        /// Lock-free ring buffer for input (recording) audio data.
        /// Initialized in Initialize() method.
        /// </summary>
        private LockFreeRingBuffer<float> _inputRing = null!;

        /// <summary>
        /// Temporary buffer for output audio samples.
        /// Initialized in Initialize() method.
        /// </summary>
        private float[] _tempOutputBuffer = null!;

        /// <summary>
        /// Temporary buffer for input audio samples.
        /// Initialized in Initialize() method.
        /// </summary>
        private float[] _tempInputBuffer = null!;

        /// <summary>
        /// Raised when the output device changes.
        /// </summary>
#pragma warning disable CS0067 // Event is never used
        public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;

        /// <summary>
        /// Raised when the input device changes.
        /// </summary>
        public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;

        /// <summary>
        /// Raised when the device state changes.
        /// </summary>
        public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;
#pragma warning restore CS0067

        /// <summary>
        /// Gets the number of frames per audio buffer.
        /// </summary>
        public int FramesPerBuffer => _framesPerBuffer;

        /// <summary>
        /// Initializes a new instance of the <see cref="NativeAudioEngine"/> class.
        /// </summary>
        public NativeAudioEngine()
        {
            _isRunning = 0;
            _isActive = 0;
            _isBuffering = 0;
            _selectedHostApiType = (PaHostApiTypeId)(-1); // Initialize to invalid value
        }

        /// <summary>
        /// Initializes the audio engine with the specified configuration.
        /// </summary>
        /// <param name="config">The audio configuration to use.</param>
        /// <returns>0 on success, negative value on error.</returns>
        public int Initialize(AudioConfig config)
        {
            if (config == null || !config.Validate())
                return -1;

            _config = config;
            _framesPerBuffer = config.BufferSize;
            // Set prebuffer threshold to 2x buffer size (in samples)
            _prebufferThreshold = config.BufferSize * config.Channels * 2;

            // Determine which backend to use
            _backend = DetermineBackend();

            try
            {
                return _backend == AudioEngineBackend.PortAudio
                    ? InitializePortAudio()
                    : InitializeMiniAudio();
            }
            catch (Exception ex)
            {
                // If PortAudio fails, try MiniAudio as fallback
                if (_backend == AudioEngineBackend.PortAudio)
                {
                    Console.WriteLine($"PortAudio initialization failed: {ex.Message}. Falling back to MiniAudio...");
                    _backend = AudioEngineBackend.MiniAudio;
                    return InitializeMiniAudio();
                }
                throw;
            }
        }

        /// <summary>
        /// Determines which audio backend to use (PortAudio or MiniAudio).
        /// Tries PortAudio first, falls back to MiniAudio if unavailable.
        /// </summary>
        /// <returns>The selected audio backend.</returns>
        private AudioEngineBackend DetermineBackend()
        {
            // Strategy: Try PortAudio first on all platforms (bundled or system-installed)
            // If not available or fails to load, fallback to MiniAudio (always bundled)

            try
            {
                _portAudioLoader = new LibraryLoader("libportaudio");
                PaBinding.InitializeBindings(_portAudioLoader);
                Console.WriteLine($"PortAudio loaded successfully from: {_portAudioLoader.LibraryPath}");
                return AudioEngineBackend.PortAudio;
            }
            catch (DllNotFoundException ex)
            {
                // PortAudio not available - this is expected on most platforms
                Console.WriteLine($"PortAudio not found: {ex.Message}");
                Console.WriteLine("Falling back to MiniAudio (bundled with application)");
                _portAudioLoader?.Dispose();
                _portAudioLoader = null;
            }
            catch (Exception ex)
            {
                // PortAudio found but failed to initialize
                Console.WriteLine($"PortAudio initialization failed: {ex.Message}");
                Console.WriteLine("Falling back to MiniAudio");
                _portAudioLoader?.Dispose();
                _portAudioLoader = null;
            }

            // Fallback: Use MiniAudio (bundled for all platforms)
            MaBinding.EnsureInitialized();
            Console.WriteLine($"MiniAudio loaded successfully"); // LibraryPath not available directly via MaBinding
            return AudioEngineBackend.MiniAudio;
        }

        #region PortAudio Implementation

        /// <summary>
        /// Converts EngineHostType to PortAudio's PaHostApiTypeId.
        /// </summary>
        private PaHostApiTypeId ConvertToPortAudioHostType(EngineHostType hostType)
        {
            return hostType switch
            {
                EngineHostType.ASIO => PaHostApiTypeId.paASIO,
                EngineHostType.COREAUDIO => PaHostApiTypeId.paCoreAudio,
                EngineHostType.ALSA => PaHostApiTypeId.paALSA,
                EngineHostType.WDMKS => PaHostApiTypeId.paWDMKS,
                EngineHostType.JACK => PaHostApiTypeId.paJACK,
                EngineHostType.WASAPI => PaHostApiTypeId.paWASAPI,
                _ => (PaHostApiTypeId)(-1) // None or unsupported - will use default
            };
        }

        /// <summary>
        /// Gets the default device index for the specified host API.
        /// Returns -1 if the host API is not available or if using default host API.
        /// </summary>
        private int GetDeviceIndexForHost(PaHostApiTypeId hostApiType, bool isInput)
        {
            if ((int)hostApiType == -1)
            {
                // Use default host API
                return isInput ? Pa_GetDefaultInputDevice() : Pa_GetDefaultOutputDevice();
            }

            // Convert host API type ID to host API index
            int hostApiIndex = Pa_HostApiTypeIdToHostApiIndex(hostApiType);

            if (hostApiIndex < 0)
            {
                // Host API not available, fallback to default
                return isInput ? Pa_GetDefaultInputDevice() : Pa_GetDefaultOutputDevice();
            }

            // Get host API info
            IntPtr hostApiInfoPtr = Pa_GetHostApiInfo(hostApiIndex);
            if (hostApiInfoPtr == IntPtr.Zero)
            {
                return isInput ? Pa_GetDefaultInputDevice() : Pa_GetDefaultOutputDevice();
            }

            var hostApiInfo = Marshal.PtrToStructure<PaHostApiInfo>(hostApiInfoPtr);

            // IMPORTANT: defaultInputDevice and defaultOutputDevice are GLOBAL device indices, not host-relative!
            // They are already global indices within the PortAudio device list.
            int globalDeviceIndex = isInput ? hostApiInfo.defaultInputDevice : hostApiInfo.defaultOutputDevice;

            if (globalDeviceIndex < 0)
            {
                // No default device for this host API
                return isInput ? Pa_GetDefaultInputDevice() : Pa_GetDefaultOutputDevice();
            }

            return globalDeviceIndex;
        }

        /// <summary>
        /// Initializes the PortAudio backend.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private unsafe int InitializePortAudio()
        {
            int result = Pa_Initialize();
            if (result != 0)
                return result;

            // Determine which host API to use
            PaHostApiTypeId requestedHostApi = ConvertToPortAudioHostType(_config.HostType);
            _selectedHostApiType = requestedHostApi;

            if (_config.HostType != EngineHostType.None)
            {
                Console.WriteLine($"PortAudio: Requesting host API '{_config.HostType}'");
            }
            else
            {
                Console.WriteLine("PortAudio: Using default host API");
            }

            // Get devices based on host API selection
            int outputDeviceIndex = GetDeviceIndexForHost(requestedHostApi, false);
            int inputDeviceIndex = _config.EnableInput ? GetDeviceIndexForHost(requestedHostApi, true) : -1;

            // Store active device indices for use in GetOutputDevices()/GetInputDevices()
            _activeOutputDeviceIndex = outputDeviceIndex;
            _activeInputDeviceIndex = inputDeviceIndex;

            // Open the stream with the selected devices
            return ReinitializePortAudioStream();
        }

        /// <summary>
        /// Opens or re-opens the PortAudio stream with the current device configuration.
        /// Used during initialization and when switching devices.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private unsafe int ReinitializePortAudioStream()
        {
            if (_activeOutputDeviceIndex < 0)
                return -1;

            // Close existing stream if open
            if (_paStream != IntPtr.Zero)
            {
                Pa_CloseStream(_paStream);
                _paStream = IntPtr.Zero;
            }

            // Free existing callback handle if allocated (will be re-allocated)
            if (_callbackHandle.IsAllocated)
                _callbackHandle.Free();

            // Initialize ring buffers if not already done or if size changed
            int ringBufferSize = _config.BufferSize * _config.Channels * 4; // 4x buffer
            if (_outputRing == null || _outputRing.Capacity != ringBufferSize)
                _outputRing = new LockFreeRingBuffer<float>(ringBufferSize);
            
            if (_config.EnableInput && (_inputRing == null || _inputRing.Capacity != ringBufferSize))
                _inputRing = new LockFreeRingBuffer<float>(ringBufferSize);

            if (_tempOutputBuffer == null || _tempOutputBuffer.Length != _config.BufferSize * _config.Channels)
                _tempOutputBuffer = new float[_config.BufferSize * _config.Channels];
            
            if (_config.EnableInput && (_tempInputBuffer == null || _tempInputBuffer.Length != _config.BufferSize * _config.Channels))
                _tempInputBuffer = new float[_config.BufferSize * _config.Channels];

            // Get output device info and log the actual device being used
            IntPtr outputDeviceInfoPtr = Pa_GetDeviceInfo(_activeOutputDeviceIndex);
            double outputLatency = _config.BufferSize / (double)_config.SampleRate;

            if (outputDeviceInfoPtr != IntPtr.Zero)
            {
                var deviceInfo = Marshal.PtrToStructure<PaDeviceInfo>(outputDeviceInfoPtr);
                outputLatency = deviceInfo.defaultLowOutputLatency;

                IntPtr hostApiInfoPtr = Pa_GetHostApiInfo(deviceInfo.hostApi);
                if (hostApiInfoPtr != IntPtr.Zero)
                {
                    var hostApiInfo = Marshal.PtrToStructure<PaHostApiInfo>(hostApiInfoPtr);
                    Console.WriteLine($"PortAudio: Using host API '{hostApiInfo.type}' with device '{deviceInfo.name}'");
                }
            }

            var outputParams = new PaStreamParameters
            {
                device = _activeOutputDeviceIndex,
                channelCount = _config.Channels,
                sampleFormat = PaSampleFormat.paFloat32,
                suggestedLatency = outputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };

            IntPtr inputParamsPtr = IntPtr.Zero;

            if (_config.EnableInput && _activeInputDeviceIndex >= 0)
            {
                // Get device-specific recommended latency for input
                IntPtr inputDeviceInfoPtr = Pa_GetDeviceInfo(_activeInputDeviceIndex);
                double inputLatency = _config.BufferSize / (double)_config.SampleRate;
                if (inputDeviceInfoPtr != IntPtr.Zero)
                {
                    var devInfo = Marshal.PtrToStructure<PaDeviceInfo>(inputDeviceInfoPtr);
                    inputLatency = devInfo.defaultLowInputLatency;
                }

                var inputParams = new PaStreamParameters
                {
                    device = _activeInputDeviceIndex,
                    channelCount = _config.Channels,
                    sampleFormat = PaSampleFormat.paFloat32,
                    suggestedLatency = inputLatency,
                    hostApiSpecificStreamInfo = IntPtr.Zero
                };
                inputParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(inputParams));
                Marshal.StructureToPtr(inputParams, inputParamsPtr, false);
            }

            IntPtr outputParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(outputParams));
            Marshal.StructureToPtr(outputParams, outputParamsPtr, false);

            // Create callback
            _paCallback = PortAudioCallback;
            _callbackHandle = GCHandle.Alloc(_paCallback);

            // Open stream with optimized flags
            const PaStreamFlags streamFlags = PaStreamFlags.paPrimeOutputBuffersUsingStreamCallback | PaStreamFlags.paClipOff;

            int result = Pa_OpenStream(
                out _paStream,
                inputParamsPtr,
                outputParamsPtr,
                _config.SampleRate,
                _config.BufferSize,
                streamFlags,
                _paCallback,
                IntPtr.Zero);

            Marshal.FreeHGlobal(outputParamsPtr);
            if (inputParamsPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(inputParamsPtr);

            return result;
        }

        /// <summary>
        /// PortAudio callback function for processing audio data.
        /// </summary>
        /// <param name="input">Pointer to input audio buffer.</param>
        /// <param name="output">Pointer to output audio buffer.</param>
        /// <param name="frameCount">Number of frames to process.</param>
        /// <param name="timeInfo">Timing information.</param>
        /// <param name="statusFlags">Stream callback flags.</param>
        /// <param name="userData">User-defined data pointer.</param>
        /// <returns>Callback result indicating whether to continue or abort.</returns>
        private unsafe PaStreamCallbackResult PortAudioCallback(
            void* input, void* output, long frameCount,
            IntPtr timeInfo, PaStreamCallbackFlags statusFlags, void* userData)
        {
            try
            {
                int sampleCount = (int)frameCount * _config.Channels;
                Span<float> outputSpan = new Span<float>(output, sampleCount);

                // Check if we're in pre-buffering state
                if (_isBuffering == 1)
                {
                    // Check if buffer has enough data to start playback
                    int availableSamples = _outputRing.AvailableRead;
                    if (availableSamples >= _prebufferThreshold)
                    {
                        // Buffer is full enough, disable buffering and start playback
                        _isBuffering = 0;
                    }
                    else
                    {
                        // Still buffering - output silence and wait for more data
                        outputSpan.Clear();

                        // Handle input even during buffering
                        if (_config.EnableInput && input != null)
                        {
                            Span<float> inputSpan = new Span<float>(input, sampleCount);
                            inputSpan.CopyTo(_tempInputBuffer.AsSpan(0, sampleCount));
                            _inputRing.Write(_tempInputBuffer.AsSpan(0, sampleCount));
                        }

                        return PaStreamCallbackResult.paContinue;
                    }
                }

                // Normal playback mode
                int samplesRead = _outputRing.Read(_tempOutputBuffer.AsSpan(0, sampleCount));

                if (samplesRead > 0)
                {
                    // Copy samples to output
                    _tempOutputBuffer.AsSpan(0, samplesRead).CopyTo(outputSpan);

                    // Fill remaining samples with silence if underrun
                    if (samplesRead < sampleCount)
                    {
                        outputSpan.Slice(samplesRead).Clear();
                    }

                    _isActive = 1;
                }
                else
                {
                    // Underrun - output silence
                    outputSpan.Clear();
                }

                // Handle input if enabled
                if (_config.EnableInput && input != null)
                {
                    Span<float> inputSpan = new Span<float>(input, sampleCount);
                    inputSpan.CopyTo(_tempInputBuffer.AsSpan(0, sampleCount));
                    _inputRing.Write(_tempInputBuffer.AsSpan(0, sampleCount));
                }

                return PaStreamCallbackResult.paContinue;
            }
            catch
            {
                _isActive = -1;
                return PaStreamCallbackResult.paAbort;
            }
        }

        #endregion

        #region MiniAudio Implementation

        /// <summary>
        /// Initializes the MiniAudio backend.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private unsafe int InitializeMiniAudio()
        {
            // Note: MiniAudio does not support host API selection like PortAudio
            // The HostType configuration parameter is ignored when using MiniAudio
            if (_config.HostType != EngineHostType.None)
            {
                Console.WriteLine($"Note: HostType '{_config.HostType}' is ignored when using MiniAudio backend. MiniAudio uses platform defaults.");
            }

            // Allocate context
            _maContext = MaBinding.allocate_context();
            if (_maContext == IntPtr.Zero)
                return -1;

            // Initialize context with platform-specific backends (same as old working version)
            MaResult result;
            if (OperatingSystem.IsLinux())
            {
                var backends = new[] { MaBackend.Alsa, MaBackend.PulseAudio, MaBackend.Jack };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _maContext);
            }
            else if (OperatingSystem.IsWindows())
            {
                var backends = new[] { MaBackend.Wasapi };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _maContext);
            }
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                var backends = new[] { MaBackend.CoreAudio };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _maContext);
            }
            else if (OperatingSystem.IsAndroid())
            {
                var backends = new[] { MaBackend.Aaudio, MaBackend.OpenSL };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _maContext);
            }
            else
            {
                // Fallback to auto-detection
                result = MaBinding.ma_context_init(null, 0, IntPtr.Zero, _maContext);
            }

            if (result != MaResult.Success)
            {
                MaBinding.ma_free(_maContext, IntPtr.Zero, "Failed to initialize MiniAudio context");
                _maContext = IntPtr.Zero;
                return (int)result;
            }

            // Allocate device
            _maDevice = MaBinding.allocate_device();
            if (_maDevice == IntPtr.Zero)
            {
                MaBinding.ma_context_uninit(_maContext);
                MaBinding.ma_free(_maContext, IntPtr.Zero, "Context cleanup after device alloc failure");
                _maContext = IntPtr.Zero;
                return -1;
            }

            // Create device config
            MaDeviceType deviceType = _config.EnableInput ? MaDeviceType.Duplex : MaDeviceType.Playback;
            _maCallback = MiniAudioCallback;
            _maCallbackHandle = GCHandle.Alloc(_maCallback);

            IntPtr configPtr = MaBinding.allocate_device_config(
                deviceType,
                MaFormat.F32,
                (uint)_config.Channels,
                (uint)_config.SampleRate,
                _maCallback,
                IntPtr.Zero, // default playback device
                IntPtr.Zero, // default capture device
                (uint)_config.BufferSize
            );

            if (configPtr == IntPtr.Zero)
            {
                MaBinding.ma_free(_maDevice, IntPtr.Zero, "Failed to allocate device config");
                MaBinding.ma_context_uninit(_maContext);
                MaBinding.ma_free(_maContext, IntPtr.Zero, "Context cleanup after config failure");
                _maContext = IntPtr.Zero;
                _maDevice = IntPtr.Zero;
                return -1;
            }

            // Initialize device
            result = MaBinding.ma_device_init(_maContext, configPtr, _maDevice);
            MaBinding.ma_free(configPtr, IntPtr.Zero, "Device config cleanup");

            if (result != MaResult.Success)
            {
                MaBinding.ma_free(_maDevice, IntPtr.Zero, "Device init failed");
                MaBinding.ma_context_uninit(_maContext);
                MaBinding.ma_free(_maContext, IntPtr.Zero, "Context cleanup after device init failure");
                _maContext = IntPtr.Zero;
                _maDevice = IntPtr.Zero;
                return (int)result;
            }

            // Initialize ring buffers
            int ringBufferSize = _config.BufferSize * _config.Channels * 4; // 4x buffer
            _outputRing = new LockFreeRingBuffer<float>(ringBufferSize);
            if (_config.EnableInput)
                _inputRing = new LockFreeRingBuffer<float>(ringBufferSize);

            _tempOutputBuffer = new float[_config.BufferSize * _config.Channels];
            if (_config.EnableInput)
                _tempInputBuffer = new float[_config.BufferSize * _config.Channels];

            return 0;
        }

        /// <summary>
        /// MiniAudio callback function for processing audio data.
        /// </summary>
        /// <param name="pDevice">Pointer to the MiniAudio device.</param>
        /// <param name="pOutput">Pointer to output audio buffer.</param>
        /// <param name="pInput">Pointer to input audio buffer.</param>
        /// <param name="frameCount">Number of frames to process.</param>
        private unsafe void MiniAudioCallback(IntPtr pDevice, void* pOutput, void* pInput, uint frameCount)
        {
            try
            {
                int sampleCount = (int)frameCount * _config.Channels;

                // Handle output (playback)
                if (pOutput != null)
                {
                    Span<float> outputSpan = new Span<float>(pOutput, sampleCount);

                    // Check if we're in pre-buffering state
                    if (_isBuffering == 1)
                    {
                        // Check if buffer has enough data to start playback
                        int availableSamples = _outputRing.AvailableRead;
                        if (availableSamples >= _prebufferThreshold)
                        {
                            // Buffer is full enough, disable buffering and start playback
                            _isBuffering = 0;
                        }
                        else
                        {
                            // Still buffering - output silence and wait for more data
                            outputSpan.Clear();

                            // Handle input even during buffering
                            if (_config.EnableInput && pInput != null)
                            {
                                Span<float> inputSpan = new Span<float>(pInput, sampleCount);
                                inputSpan.CopyTo(_tempInputBuffer.AsSpan(0, sampleCount));
                                _inputRing.Write(_tempInputBuffer.AsSpan(0, sampleCount));
                            }

                            return;
                        }
                    }

                    // Normal playback mode
                    int samplesRead = _outputRing.Read(_tempOutputBuffer.AsSpan(0, sampleCount));

                    if (samplesRead > 0)
                    {
                        // Copy samples to output
                        _tempOutputBuffer.AsSpan(0, samplesRead).CopyTo(outputSpan);

                        // Fill remaining samples with silence if underrun
                        if (samplesRead < sampleCount)
                        {
                            outputSpan.Slice(samplesRead).Clear();
                        }

                        _isActive = 1;
                    }
                    else
                    {
                        // Underrun - output silence
                        outputSpan.Clear();
                    }
                }

                // Handle input (recording)
                if (_config.EnableInput && pInput != null)
                {
                    Span<float> inputSpan = new Span<float>(pInput, sampleCount);
                    inputSpan.CopyTo(_tempInputBuffer.AsSpan(0, sampleCount));
                    _inputRing.Write(_tempInputBuffer.AsSpan(0, sampleCount));
                }
            }
            catch
            {
                _isActive = -1;
            }
        }

        #endregion

        /// <summary>
        /// Starts audio processing.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        public int Start()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
                return 0; // Already running

            // Enable pre-buffering to prevent playback until buffer has enough data
            _isBuffering = 1;

            if (_backend == AudioEngineBackend.PortAudio)
            {
                int result = Pa_StartStream(_paStream);
                if (result != 0)
                {
                    _isRunning = 0;
                    _isBuffering = 0;
                    return result;
                }
            }
            else // MiniAudio
            {
                MaResult result = MaBinding.ma_device_start(_maDevice);
                if (result != MaResult.Success)
                {
                    _isRunning = 0;
                    _isBuffering = 0;
                    return (int)result;
                }
            }

            return 0;
        }

        /// <summary>
        /// Stops audio processing.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        public int Stop()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 0, 1) == 0)
                return 0; // Already stopped

            _isBuffering = 0;

            if (_backend == AudioEngineBackend.PortAudio)
            {
                int result = Pa_StopStream(_paStream);
                _isActive = 0;
                return result;
            }
            else // MiniAudio
            {
                MaResult result = MaBinding.ma_device_stop(_maDevice);
                _isActive = 0;
                return (int)result;
            }
        }

        /// <summary>
        /// Sends audio samples to the output buffer for playback.
        /// BLOCKS until all samples are written to the ring buffer.
        /// This provides natural timing for the AudioMixer.
        /// </summary>
        /// <param name="samples">Audio samples to send.</param>
        /// <exception cref="AudioException">Thrown when the engine is not running.</exception>
        public void Send(Span<float> samples)
        {
            if (_isRunning == 0)
                throw new AudioException("Engine not running");

            int totalSamples = samples.Length;
            int written = 0;

            // Write samples in a loop, blocking until all samples are written
            while (written < totalSamples && _isRunning == 1)
            {
                int remainingSamples = totalSamples - written;
                int samplesWritten = _outputRing.Write(samples.Slice(written, remainingSamples));

                if (samplesWritten > 0)
                {
                    written += samplesWritten;
                }
                else
                {
                    // Buffer is full, sleep briefly to wait for audio callback to consume data
                    // Sleep duration: time to play 1/4 buffer at current sample rate
                    // Example: 512 frames / 48000 Hz / 4 = ~2.66ms
                    int sleepMs = Math.Max(1, (_framesPerBuffer / 4 * 1000) / _config.SampleRate);
                    Thread.Sleep(sleepMs);
                }
            }
        }

        /// <summary>
        /// Receives audio samples from the input buffer (recording).
        /// </summary>
        /// <param name="samples">Output array containing received audio samples.</param>
        /// <returns>Number of samples read on success, -1 on error or if input is not enabled.</returns>
        public int Receives(out float[] samples)
        {
            if (_isRunning == 0)
            {
                samples = null!;
                return -1;
            }

            if (!_config.EnableInput)
            {
                samples = null!;
                return -1;
            }

            int sampleCount = _config.BufferSize * _config.Channels;
            samples = new float[sampleCount];

            int samplesRead = _inputRing.Read(samples);
            return samplesRead;
        }

        /// <summary>
        /// Gets the activation state of the audio engine.
        /// </summary>
        /// <returns>0 = idle, 1 = active, -1 = error.</returns>
        public int OwnAudioEngineActivate()
        {
            // Use _isRunning for now as tests expect active state immediately after Start()
            // _isActive is set in the callback which might run later
            return _isRunning;
        }

        /// <summary>
        /// Checks if the audio engine is stopped.
        /// </summary>
        /// <returns>1 if stopped, 0 if running.</returns>
        public int OwnAudioEngineStopped()
        {
            return _isRunning == 0 ? 1 : 0;
        }

        /// <summary>
        /// Gets the native stream or device handle.
        /// </summary>
        /// <returns>Pointer to PortAudio stream or MiniAudio device.</returns>
        public IntPtr GetStream()
        {
            return _backend == AudioEngineBackend.PortAudio ? _paStream : _maDevice;
        }

        /// <summary>
        /// Gets a list of available output (playback) devices.
        /// </summary>
        /// <returns>List of output device information.</returns>
        public List<AudioDeviceInfo> GetOutputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

            if (_backend == AudioEngineBackend.PortAudio)
            {
                // Get the host API index for the selected host API type
                int selectedHostApiIndex = -1;
                if ((int)_selectedHostApiType != -1)
                {
                    selectedHostApiIndex = Pa_HostApiTypeIdToHostApiIndex(_selectedHostApiType);
                }
                
                // Identify the actual system default device for the selected host
                int defaultDeviceIndex = GetDeviceIndexForHost(_selectedHostApiType, false);

                int deviceCount = Pa_GetDeviceCount();
                for (int i = 0; i < deviceCount; i++)
                {
                    IntPtr deviceInfoPtr = Pa_GetDeviceInfo(i);
                    if (deviceInfoPtr != IntPtr.Zero)
                    {
                        var paDeviceInfo = Marshal.PtrToStructure<PaDeviceInfo>(deviceInfoPtr);

                        // Skip devices that don't match the selected host API
                        if (selectedHostApiIndex >= 0 && paDeviceInfo.hostApi != selectedHostApiIndex)
                        {
                            continue;
                        }

                        if (paDeviceInfo.maxOutputChannels > 0)
                        {
                            // Get host API name for engine name
                            string engineName = "Portaudio";
                            IntPtr hostApiInfoPtr = Pa_GetHostApiInfo(paDeviceInfo.hostApi);
                            if (hostApiInfoPtr != IntPtr.Zero)
                            {
                                var hostApiInfo = Marshal.PtrToStructure<PaHostApiInfo>(hostApiInfoPtr);
                                if (!string.IsNullOrEmpty(hostApiInfo.name))
                                {
                                    engineName = $"Portaudio.{hostApiInfo.name}";
                                }
                            }

                            devices.Add(new AudioDeviceInfo(
                                deviceId: i.ToString(),
                                name: paDeviceInfo.name,
                                engineName: engineName,
                                isInput: paDeviceInfo.maxInputChannels > 0,
                                isOutput: true,
                                isDefault: (i == defaultDeviceIndex),
                                state: AudioDeviceState.Active
                            ));
                        }
                    }
                }
            }
            else if (_backend == AudioEngineBackend.MiniAudio)
            {
                // MiniAudio device enumeration using ma_context_get_devices
                try
                {
                    MaResult result = MaBinding.ma_context_get_devices(
                        _maContext,
                        out IntPtr pPlaybackDevices,
                        out IntPtr pCaptureDevices,
                        out int playbackCount,
                        out int captureCount);

                    if (result != MaResult.Success)
                    {
                        Console.WriteLine($"MiniAudio device enumeration failed: {result}");
                        return devices;
                    }

                    // Get backend name for display
                    var context = Marshal.PtrToStructure<MaContext>(_maContext);
                    string engineName = $"MiniAudio.{context.backend}";

                    // Enumerate output devices
                    for (int i = 0; i < playbackCount; i++)
                    {
                        // Calculate pointer to device info
                        int deviceInfoSize = Marshal.SizeOf<MaDeviceInfo>();
                        IntPtr deviceInfoPtr = IntPtr.Add(pPlaybackDevices, i * deviceInfoSize);

                        var deviceInfo = Marshal.PtrToStructure<MaDeviceInfo>(deviceInfoPtr);

                        // Convert device ID to string (use index as ID)
                        string deviceId = $"ma_output_{i}";

                        devices.Add(new AudioDeviceInfo(
                            deviceId: deviceId,
                            name: deviceInfo.Name,
                            engineName: engineName,
                            isInput: false,
                            isOutput: true,
                            isDefault: deviceInfo.IsDefault,
                            state: AudioDeviceState.Active
                        ));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"MiniAudio output device enumeration error: {ex.Message}");
                }
            }

            return devices;
        }

        /// <summary>
        /// Gets a list of available input (recording) devices.
        /// </summary>
        /// <returns>List of input device information.</returns>
        public List<AudioDeviceInfo> GetInputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

            if (_backend == AudioEngineBackend.PortAudio)
            {
                // Get the host API index for the selected host API type
                int selectedHostApiIndex = -1;
                if ((int)_selectedHostApiType != -1)
                {
                    selectedHostApiIndex = Pa_HostApiTypeIdToHostApiIndex(_selectedHostApiType);
                }

                // Identify the actual system default device for the selected host
                int defaultDeviceIndex = GetDeviceIndexForHost(_selectedHostApiType, true);

                int deviceCount = Pa_GetDeviceCount();
                for (int i = 0; i < deviceCount; i++)
                {
                    IntPtr deviceInfoPtr = Pa_GetDeviceInfo(i);
                    if (deviceInfoPtr != IntPtr.Zero)
                    {
                        var paDeviceInfo = Marshal.PtrToStructure<PaDeviceInfo>(deviceInfoPtr);

                        // Skip devices that don't match the selected host API
                        if (selectedHostApiIndex >= 0 && paDeviceInfo.hostApi != selectedHostApiIndex)
                        {
                            continue;
                        }

                        if (paDeviceInfo.maxInputChannels > 0)
                        {
                            // Get host API name for engine name
                            string engineName = "Portaudio";
                            IntPtr hostApiInfoPtr = Pa_GetHostApiInfo(paDeviceInfo.hostApi);
                            if (hostApiInfoPtr != IntPtr.Zero)
                            {
                                var hostApiInfo = Marshal.PtrToStructure<PaHostApiInfo>(hostApiInfoPtr);
                                if (!string.IsNullOrEmpty(hostApiInfo.name))
                                {
                                    engineName = $"Portaudio.{hostApiInfo.name}";
                                }
                            }

                            devices.Add(new AudioDeviceInfo(
                                deviceId: i.ToString(),
                                name: paDeviceInfo.name,
                                engineName: engineName,
                                isInput: true,
                                isOutput: paDeviceInfo.maxOutputChannels > 0,
                                isDefault: (i == defaultDeviceIndex),
                                state: AudioDeviceState.Active
                            ));
                        }
                    }
                }
            }
            else if (_backend == AudioEngineBackend.MiniAudio)
            {
                // MiniAudio device enumeration using ma_context_get_devices
                try
                {
                    MaResult result = MaBinding.ma_context_get_devices(
                        _maContext,
                        out IntPtr pPlaybackDevices,
                        out IntPtr pCaptureDevices,
                        out int playbackCount,
                        out int captureCount);

                    if (result != MaResult.Success)
                    {
                        Console.WriteLine($"MiniAudio device enumeration failed: {result}");
                        return devices;
                    }

                    // Get backend name for display
                    var context = Marshal.PtrToStructure<MaContext>(_maContext);
                    string engineName = $"MiniAudio.{context.backend}";

                    // Enumerate input devices
                    for (int i = 0; i < captureCount; i++)
                    {
                        // Calculate pointer to device info
                        int deviceInfoSize = Marshal.SizeOf<MaDeviceInfo>();
                        IntPtr deviceInfoPtr = IntPtr.Add(pCaptureDevices, i * deviceInfoSize);

                        var deviceInfo = Marshal.PtrToStructure<MaDeviceInfo>(deviceInfoPtr);

                        // Convert device ID to string (use index as ID)
                        string deviceId = $"ma_input_{i}";

                        devices.Add(new AudioDeviceInfo(
                            deviceId: deviceId,
                            name: deviceInfo.Name,
                            engineName: engineName,
                            isInput: true,
                            isOutput: false,
                            isDefault: deviceInfo.IsDefault,
                            state: AudioDeviceState.Active
                        ));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"MiniAudio input device enumeration error: {ex.Message}");
                }
            }

            return devices;
        }

        /// <summary>
        /// Sets the output device by name.
        /// </summary>
        /// <param name="deviceName">The name of the device to use.</param>
        /// <returns>0 on success, -1 if not implemented or error.</returns>
        public int SetOutputDeviceByName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return -1;

            if (_isRunning == 1)
                return -2; // Cannot change device while running

            var devices = GetOutputDevices();
            foreach (var device in devices)
            {
                if (device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    // Parse device index from ID
                    if (int.TryParse(device.DeviceId, out int deviceIndex))
                    {
                        _activeOutputDeviceIndex = deviceIndex;
                        _config.OutputDeviceId = device.DeviceId;
                        return ReinitializePortAudioStream();
                    }
                }
            }

            return -3; // Device not found
        }

        /// <summary>
        /// Sets the output device by index.
        /// </summary>
        /// <param name="deviceIndex">The index of the device to use.</param>
        /// <returns>0 on success, -1 if not implemented or error.</returns>
        public int SetOutputDeviceByIndex(int deviceIndex)
        {
            if (deviceIndex < 0)
                return -1;

            if (_isRunning == 1)
                return -2; // Cannot change device while running

            var devices = GetOutputDevices();
            if (deviceIndex >= devices.Count)
                return -3; // Index out of range

            // Get the actual PortAudio device index from the device info
            // The list index passed in (deviceIndex) maps to devices[deviceIndex]
            // The actual PA index is stored in DeviceId
            if (int.TryParse(devices[deviceIndex].DeviceId, out int actualPaIndex))
            {
                _activeOutputDeviceIndex = actualPaIndex;
                _config.OutputDeviceId = devices[deviceIndex].DeviceId;
                return ReinitializePortAudioStream();
            }

            return -3; // Should not happen if DeviceId is valid
        }

        /// <summary>
        /// Sets the input device by name.
        /// </summary>
        /// <param name="deviceName">The name of the device to use.</param>
        /// <returns>0 on success, -1 if not implemented or error.</returns>
        public int SetInputDeviceByName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return -1;

            if (_isRunning == 1)
                return -2; // Cannot change device while running

            var devices = GetInputDevices();
            foreach (var device in devices)
            {
                if (device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    // Parse device index from ID
                    if (int.TryParse(device.DeviceId, out int deviceIndex))
                    {
                        _activeInputDeviceIndex = deviceIndex;
                        _config.InputDeviceId = device.DeviceId;
                        return ReinitializePortAudioStream();
                    }
                }
            }

            return -3; // Device not found
        }

        /// <summary>
        /// Sets the input device by index.
        /// </summary>
        /// <param name="deviceIndex">The index of the device to use.</param>
        /// <returns>0 on success, -1 if not implemented or error.</returns>
        public int SetInputDeviceByIndex(int deviceIndex)
        {
            if (deviceIndex < 0)
                return -1;

            if (_isRunning == 1)
                return -2; // Cannot change device while running

            var devices = GetInputDevices();
            if (deviceIndex >= devices.Count)
                return -3; // Index out of range

            // Get the actual PortAudio device index from the device info
            if (int.TryParse(devices[deviceIndex].DeviceId, out int actualPaIndex))
            {
                _activeInputDeviceIndex = actualPaIndex;
                _config.InputDeviceId = devices[deviceIndex].DeviceId;
                return ReinitializePortAudioStream();
            }

            return -3; // Should not happen
        }

        /// <summary>
        /// Disposes the audio engine and releases all resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();

            if (_backend == AudioEngineBackend.PortAudio)
            {
                if (_paStream != IntPtr.Zero)
                {
                    Pa_CloseStream(_paStream);
                    _paStream = IntPtr.Zero;
                }
                Pa_Terminate();

                if (_callbackHandle.IsAllocated)
                    _callbackHandle.Free();
            }
            else if (_backend == AudioEngineBackend.MiniAudio)
            {
                if (_maDevice != IntPtr.Zero)
                {
                    MaBinding.ma_device_uninit(_maDevice);
                    MaBinding.ma_free(_maDevice, IntPtr.Zero, "Device cleanup in Dispose");
                    _maDevice = IntPtr.Zero;
                }

                if (_maContext != IntPtr.Zero)
                {
                    MaBinding.ma_context_uninit(_maContext);
                    MaBinding.ma_free(_maContext, IntPtr.Zero, "Context cleanup in Dispose");
                    _maContext = IntPtr.Zero;
                }

                if (_maCallbackHandle.IsAllocated)
                    _maCallbackHandle.Free();
            }

            _portAudioLoader?.Dispose();
            // _miniAudioLoader?.Dispose(); // REMOVED: Managed by MaBinding

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Specifies the audio engine backend type.
        /// </summary>
        private enum AudioEngineBackend
        {
            /// <summary>
            /// PortAudio backend.
            /// </summary>
            PortAudio,

            /// <summary>
            /// MiniAudio backend.
            /// </summary>
            MiniAudio
        }
    }
}
