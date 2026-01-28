using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Logger;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Native.MiniAudio;
using static Ownaudio.Native.MiniAudio.MaBinding;

namespace Ownaudio.Native
{
    public sealed partial class NativeAudioEngine
    {
        #region MiniAudio Fields

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

        #endregion

        #region MiniAudio Implementation

        /// <summary>
        /// Initializes the MiniAudio backend.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private unsafe int InitializeMiniAudio()
        {
            if (_config.HostType != EngineHostType.None)
            {
                Log.Warning($"Note: HostType '{_config.HostType}' is ignored when using MiniAudio backend. MiniAudio uses platform defaults.");
            }

            // Allocate context
            _maContext = MaBinding.allocate_context();
            if (_maContext == IntPtr.Zero)
                return -1;

            // Initialize context with platform-specific backends
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
                                _inputRing.Write(inputSpan);
                            }

                            return;
                        }
                    }

                    // Normal playback mode - Zero-copy: read directly to output
                    int samplesRead = _outputRing.Read(outputSpan);

                    if (samplesRead > 0)
                    {
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

                // Handle input (recording) - Zero-copy: write directly from input
                if (_config.EnableInput && pInput != null)
                {
                    Span<float> inputSpan = new Span<float>(pInput, sampleCount);
                    _inputRing.Write(inputSpan);
                }
            }
            catch
            {
                _isActive = -1;
            }
        }

        /// <summary>
        /// Reinitializes the MiniAudio device with current device configuration.
        /// Used when switching devices at runtime.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private unsafe int ReinitializeMiniAudioDevice()
        {
            // Only stop the device if it's actually running
            if (_maDevice != IntPtr.Zero && _isRunning == 1)
            {
                MaBinding.ma_device_stop(_maDevice);
            }

            // Uninitialize and free the device if it exists
            if (_maDevice != IntPtr.Zero)
            {
                MaBinding.ma_device_uninit(_maDevice);
                MaBinding.ma_free(_maDevice, IntPtr.Zero, "Device cleanup for reinitialization");
                _maDevice = IntPtr.Zero;
            }

            // Free existing callback handle if allocated (will be re-allocated)
            if (_maCallbackHandle.IsAllocated)
                _maCallbackHandle.Free();

            // Allocate new device
            _maDevice = MaBinding.allocate_device();
            if (_maDevice == IntPtr.Zero)
                return -1;

            // Initialize ring buffers if not already done or if size changed
            int ringBufferSize = _config.BufferSize * _config.Channels * 4; // 4x buffer
            if (_outputRing == null || _outputRing.Capacity != ringBufferSize)
                _outputRing = new LockFreeRingBuffer<float>(ringBufferSize);

            if (_config.EnableInput && (_inputRing == null || _inputRing.Capacity != ringBufferSize))
                _inputRing = new LockFreeRingBuffer<float>(ringBufferSize);

            // Create device config
            MaDeviceType deviceType = _config.EnableInput ? MaDeviceType.Duplex : MaDeviceType.Playback;
            _maCallback = MiniAudioCallback;
            _maCallbackHandle = GCHandle.Alloc(_maCallback);

            // Parse device IDs from config if specified
            IntPtr playbackDeviceIdPtr = IntPtr.Zero;
            IntPtr captureDeviceIdPtr = IntPtr.Zero;

            // For MiniAudio, device IDs are stored as "ma_output_X" or "ma_input_X" format
            // We need to get the actual device ID from the enumeration
            if (!string.IsNullOrEmpty(_config.OutputDeviceId) && _config.OutputDeviceId.StartsWith("ma_output_"))
            {
                // Extract index from device ID
                if (int.TryParse(_config.OutputDeviceId.Substring("ma_output_".Length), out int deviceIndex))
                {
                    // Get device list and extract the actual device ID
                    MaResult enumResult = MaBinding.ma_context_get_devices(
                        _maContext,
                        out IntPtr pPlaybackDevices,
                        out IntPtr pCaptureDevices,
                        out int playbackCount,
                        out int captureCount);

                    if (enumResult == MaResult.Success && deviceIndex < playbackCount)
                    {
                        int deviceInfoSize = Marshal.SizeOf<MaDeviceInfo>();
                        IntPtr deviceInfoPtr = IntPtr.Add(pPlaybackDevices, deviceIndex * deviceInfoSize);
                        var deviceInfo = Marshal.PtrToStructure<MaDeviceInfo>(deviceInfoPtr);

                        // Allocate and copy device ID
                        playbackDeviceIdPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MaDeviceId>());
                        Marshal.StructureToPtr(deviceInfo.Id, playbackDeviceIdPtr, false);
                    }
                }
            }

            if (_config.EnableInput && !string.IsNullOrEmpty(_config.InputDeviceId) && _config.InputDeviceId.StartsWith("ma_input_"))
            {
                // Extract index from device ID
                if (int.TryParse(_config.InputDeviceId.Substring("ma_input_".Length), out int deviceIndex))
                {
                    // Get device list and extract the actual device ID
                    MaResult enumResult = MaBinding.ma_context_get_devices(
                        _maContext,
                        out IntPtr pPlaybackDevices,
                        out IntPtr pCaptureDevices,
                        out int playbackCount,
                        out int captureCount);

                    if (enumResult == MaResult.Success && deviceIndex < captureCount)
                    {
                        int deviceInfoSize = Marshal.SizeOf<MaDeviceInfo>();
                        IntPtr deviceInfoPtr = IntPtr.Add(pCaptureDevices, deviceIndex * deviceInfoSize);
                        var deviceInfo = Marshal.PtrToStructure<MaDeviceInfo>(deviceInfoPtr);

                        // Allocate and copy device ID
                        captureDeviceIdPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MaDeviceId>());
                        Marshal.StructureToPtr(deviceInfo.Id, captureDeviceIdPtr, false);
                    }
                }
            }

            IntPtr configPtr = MaBinding.allocate_device_config(
                deviceType,
                MaFormat.F32,
                (uint)_config.Channels,
                (uint)_config.SampleRate,
                _maCallback,
                playbackDeviceIdPtr,
                captureDeviceIdPtr,
                (uint)_config.BufferSize
            );

            if (configPtr == IntPtr.Zero)
            {
                if (playbackDeviceIdPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(playbackDeviceIdPtr);
                if (captureDeviceIdPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(captureDeviceIdPtr);
                MaBinding.ma_free(_maDevice, IntPtr.Zero, "Failed to allocate device config");
                _maDevice = IntPtr.Zero;
                return -1;
            }

            // Initialize device
            MaResult result = MaBinding.ma_device_init(_maContext, configPtr, _maDevice);
            MaBinding.ma_free(configPtr, IntPtr.Zero, "Device config cleanup");

            // Free device ID pointers
            if (playbackDeviceIdPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(playbackDeviceIdPtr);
            if (captureDeviceIdPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(captureDeviceIdPtr);

            if (result != MaResult.Success)
            {
                MaBinding.ma_free(_maDevice, IntPtr.Zero, "Device init failed");
                _maDevice = IntPtr.Zero;
                return (int)result;
            }

            Log.Info($"MiniAudio device reinitialized successfully");
            return 0;
        }

        /// <summary>
        /// Gets output devices using MiniAudio backend.
        /// </summary>
        /// <returns>List of output device information.</returns>
        private List<AudioDeviceInfo> GetMiniAudioOutputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

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
                    Log.Error($"MiniAudio device enumeration failed: {result}");
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

                    // Extract max channel counts from nativeDataFormats
                    int maxOutputChannels = 0;
                    int maxInputChannels = 0;

                    if (deviceInfo.nativeDataFormats != null && deviceInfo.NativeDataFormatCount > 0)
                    {
                        for (int j = 0; j < deviceInfo.NativeDataFormatCount && j < deviceInfo.nativeDataFormats.Length; j++)
                        {
                            var format = deviceInfo.nativeDataFormats[j];
                            if (format.channels > maxOutputChannels)
                                maxOutputChannels = (int)format.channels;
                        }
                    }

                    devices.Add(new AudioDeviceInfo(
                        deviceId: deviceId,
                        name: deviceInfo.Name,
                        engineName: engineName,
                        isInput: false,
                        isOutput: true,
                        isDefault: deviceInfo.IsDefault,
                        state: AudioDeviceState.Active,
                        maxInputChannels: maxInputChannels,
                        maxOutputChannels: maxOutputChannels
                    ));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"MiniAudio output device enumeration error: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// Gets input devices using MiniAudio backend.
        /// </summary>
        /// <returns>List of input device information.</returns>
        private List<AudioDeviceInfo> GetMiniAudioInputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

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
                    Log.Error($"MiniAudio device enumeration failed: {result}");
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

                    // Extract max channel counts from nativeDataFormats
                    int maxInputChannels = 0;
                    int maxOutputChannels = 0;

                    if (deviceInfo.nativeDataFormats != null && deviceInfo.NativeDataFormatCount > 0)
                    {
                        for (int j = 0; j < deviceInfo.NativeDataFormatCount && j < deviceInfo.nativeDataFormats.Length; j++)
                        {
                            var format = deviceInfo.nativeDataFormats[j];
                            if (format.channels > maxInputChannels)
                                maxInputChannels = (int)format.channels;
                        }
                    }

                    devices.Add(new AudioDeviceInfo(
                        deviceId: deviceId,
                        name: deviceInfo.Name,
                        engineName: engineName,
                        isInput: true,
                        isOutput: false,
                        isDefault: deviceInfo.IsDefault,
                        state: AudioDeviceState.Active,
                        maxInputChannels: maxInputChannels,
                        maxOutputChannels: maxOutputChannels
                    ));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"MiniAudio input device enumeration error: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// Gets the raw device count from MiniAudio backend.
        /// </summary>
        /// <returns>Total number of devices (playback + capture).</returns>
        private int GetMiniAudioRawDeviceCount()
        {
            MaBinding.ma_context_get_devices(
                _maContext,
                out _,
                out _,
                out int playbackCount,
                out int captureCount);

            return playbackCount + captureCount;
        }

        /// <summary>
        /// Starts MiniAudio playback.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private int StartMiniAudio()
        {
            MaResult result = MaBinding.ma_device_start(_maDevice);
            return result == MaResult.Success ? 0 : (int)result;
        }

        /// <summary>
        /// Stops MiniAudio playback.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private int StopMiniAudio()
        {
            MaResult result = MaBinding.ma_device_stop(_maDevice);
            return (int)result;
        }

        /// <summary>
        /// Disposes MiniAudio resources.
        /// </summary>
        private void DisposeMiniAudio()
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

        #endregion
    }
}
