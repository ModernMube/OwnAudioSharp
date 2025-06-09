using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Ownaudio.Bindings.Miniaudio;
using Ownaudio.Exceptions;
using static Ownaudio.Bindings.Miniaudio.MaBinding;

namespace Ownaudio.MiniAudio
{
    /// <summary>
    /// Miniaudio-based audio engine.
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
        #endregion

        #region Public properties
        /// <summary>
        /// Sample rate used by the audio engine.
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Number of channels used by the audio engine.
        /// </summary>
        public int Channels => _channels;

        /// <summary>
        /// Sample format used by the audio engine.
        /// </summary>
        internal MaFormat SampleFormat => _sampleFormat;

        /// <summary>
        /// List of available playback devices.
        /// </summary>
        public IReadOnlyList<DeviceInfo> PlaybackDevices => _playbackDevices;

        /// <summary>
        /// List of available capture devices.
        /// </summary>
        public IReadOnlyList<DeviceInfo> CaptureDevices => _captureDevices;

        /// <summary>
        /// Currently active playback device.
        /// </summary>
        public DeviceInfo? CurrentPlaybackDevice { get; private set; }

        /// <summary>
        /// Currently active capture device.
        /// </summary>
        public DeviceInfo? CurrentCaptureDevice { get; private set; }

        /// <summary>
        /// Event triggered when audio data is being processed.
        /// </summary>
        public event EventHandler<AudioDataEventArgs>? AudioProcessing;
        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="MiniAudioEngine"/> class with default settings.
        /// </summary>
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
            else if(OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                var backends = new[] { MaBackend.CoreAudio };
                result = MaBinding.ma_context_init(backends, (uint)backends.Length, IntPtr.Zero, _context);
            }
            else if(OperatingSystem.IsAndroid())
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
        /// Creates a new MiniAudioEngine instance.
        /// </summary>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="deviceType">Type of audio device.</param>
        /// <param name="sampleFormat">Sample format.</param>
        /// <param name="channels">Number of channels.</param>
        /// <param name="sizeInFrame">Period size in frame</param>
        public MiniAudioEngine(int sampleRate = 44100, EngineDeviceType deviceType = EngineDeviceType.Playback,
            EngineAudioFormat sampleFormat = EngineAudioFormat.F32, int channels = 2, int sizeInFrame = 512)
        {
            _sampleRate = sampleRate;
            _deviceType = (MaDeviceType)deviceType;
            _sampleFormat = (MaFormat)sampleFormat;
            _channels = channels;
            _sizeInFrame = sizeInFrame;

            InitializeAudioDevice();
        }

        /// <summary>
        /// Initializes the audio device context and default device.
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
        /// Initializes a specific audio device.
        /// </summary>
        /// <param name="deviceId">Device ID pointer, or IntPtr.Zero for default device.</param>
        /// <param name="type">Type of the device (playback, capture, etc).</param>
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
                if(deviceId == IntPtr.Zero)
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
        /// Optimized audio callback that minimizes GC pressure by reusing buffers.
        /// </summary>
        /// <param name="pDevice">Pointer to the audio device.</param>
        /// <param name="pOutput">Pointer to the output buffer.</param>
        /// <param name="pInput">Pointer to the input buffer.</param>
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
                ProcessAudioBuffer(pOutput, sampleCount, AudioDataDirection.Output,
                    ref _outputBuffer, ref _outputEventArgs, ref _lastOutputBufferSize);
            }

            if (_deviceType != MaDeviceType.Playback && pInput != null)
            {
                ProcessAudioBuffer(pInput, sampleCount, AudioDataDirection.Input,
                    ref _inputBuffer, ref _inputEventArgs, ref _lastInputBufferSize);
            }
        }


        /// <summary>
        /// Helper method to process audio buffers while minimizing allocations.
        /// </summary>
        /// <param name="pBuffer">Pointer to the audio buffer.</param>
        /// <param name="sampleCount">Number of samples to process.</param>
        /// <param name="direction">Direction of audio data flow.</param>
        /// <param name="buffer">Reference to the buffer array.</param>
        /// <param name="eventArgs">Reference to the event arguments.</param>
        /// <param name="lastBufferSize">Reference to the last buffer size.</param>
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
        /// Updates the lists of available audio devices.
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
        /// <param name="type">Type of the device.</param>
        /// <exception cref="InvalidOperationException">Thrown if device switching fails.</exception>
        public void SwitchDevice(DeviceInfo deviceInfo, EngineDeviceType type = EngineDeviceType.Playback)
        {
            if (deviceInfo.Id == IntPtr.Zero)
                throw new InvalidOperationException("Failed to switch device. Invalid device ID.");

            InitializeDeviceInternal(deviceInfo.Id, (MaDeviceType)type);
        }

        /// <summary>
        /// Creates a new audio decoder for the specified stream.
        /// </summary>
        /// <param name="stream">The audio stream to decode.</param>
        /// <returns>A new MiniAudioDecoder instance.</returns>
        public MiniAudioDecoder CreateDecoder(Stream stream)
        {
            return new MiniAudioDecoder(stream, (EngineAudioFormat)_sampleFormat, _channels, _sampleRate);
        }

        /// <summary>
        /// Starts audio processing on the current device.
        /// </summary>
        public void Start()
        {
            if (_device != IntPtr.Zero)
                MaBinding.ma_device_start(_device);
        }

        /// <summary>
        /// Stops audio processing on the current device.
        /// </summary>
        public void Stop()
        {
            if (_device != IntPtr.Zero)
                MaBinding.ma_device_stop(_device);
        }

        /// <summary>
        /// Checks if the audio engine is currently running.
        /// </summary>
        /// <returns>True if the audio engine is running, false otherwise.</returns>
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
        /// The device identifier.
        /// </summary>
        public IntPtr Id { get; internal set; }

        /// <summary>
        /// The name of the device.
        /// </summary>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>
        /// Indicates whether this is the default device.
        /// </summary>
        public bool IsDefault { get; internal set; }
    }

    /// <summary>
    /// Direction of audio data flow.
    /// </summary>
    public enum AudioDataDirection
    {
        /// <summary>
        /// Output (playback) data.
        /// </summary>
        Output,

        /// <summary>
        /// Input (recording) data.
        /// </summary>
        Input
    }

    /// <summary>
    /// Event arguments for audio data processing events.
    /// </summary>
    public class AudioDataEventArgs : EventArgs
    {
        /// <summary>
        /// The audio buffer containing the samples.
        /// </summary>
        public float[] Buffer { get; }

        /// <summary>
        /// The number of valid samples in the buffer.
        /// </summary>
        public int SampleCount { get; internal set; }

        /// <summary>
        /// The direction of the audio data flow.
        /// </summary>
        public AudioDataDirection Direction { get; }

        /// <summary>
        /// Creates a new instance of AudioDataEventArgs.
        /// </summary>
        public AudioDataEventArgs(float[] buffer, AudioDataDirection direction, int sampleCount)
        {
            Buffer = buffer;
            SampleCount = sampleCount;
            Direction = direction;
        }
    }

    /// <summary>
    /// Types of audio devices.
    /// </summary>
    public enum EngineDeviceType
    {
        /// <summary>
        /// Device for audio playback.
        /// </summary>
        Playback = 1,

        /// <summary>
        /// Device for audio capture/recording.
        /// </summary>
        Capture = 2,

        /// <summary>
        /// Device for both playback and capture.
        /// </summary>
        Duplex = 3,

        /// <summary>
        /// Device for loopback recording.
        /// </summary>
        Loopback = 4
    }

    /// <summary>
    /// Audio sample formats.
    /// </summary>
    public enum EngineAudioFormat
    {
        /// <summary>
        /// Unknown format.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// 8-bit unsigned format.
        /// </summary>
        U8 = 1,

        /// <summary>
        /// 16-bit signed format.
        /// </summary>
        S16 = 2,

        /// <summary>
        /// 24-bit signed format.
        /// </summary>
        S24 = 3,

        /// <summary>
        /// 32-bit signed format.
        /// </summary>
        S32 = 4,

        /// <summary>
        /// 32-bit floating point format.
        /// </summary>
        F32 = 5,

        /// <summary>
        /// Number of supported formats.
        /// </summary>
        Count
    }
}
