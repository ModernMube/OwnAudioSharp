using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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
            // Use physical channel count so the device is opened with enough channels
            // to cover the highest-numbered channel selector (e.g. [4,5] needs 6 physical channels).
            MaDeviceType deviceType = _config.EnableInput ? MaDeviceType.Duplex : MaDeviceType.Playback;
            _maCallback = MiniAudioCallback;
            _maCallbackHandle = GCHandle.Alloc(_maCallback);

            uint physicalOutChannels = (uint)_physicalOutputChannels;
            uint physicalInChannels = (uint)(_config.EnableInput ? _physicalInputChannels : _physicalOutputChannels);

            ResolveMiniAudioDeviceIds(out IntPtr playbackDeviceIdPtr, out IntPtr captureDeviceIdPtr);

            IntPtr configPtr = MaBinding.allocate_device_config(
                deviceType,
                MaFormat.F32,
                physicalOutChannels,
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
                MaBinding.ma_context_uninit(_maContext);
                MaBinding.ma_free(_maContext, IntPtr.Zero, "Context cleanup after config failure");
                _maContext = IntPtr.Zero;
                _maDevice = IntPtr.Zero;
                return -1;
            }

            // The managed MaResamplerConfig uses IntPtr (8 bytes) for allocationCallbacks,
            // but the native ma_allocation_callbacks is 32 bytes (4 function pointers).
            // This 8-byte shortfall shifts every field after 'resampling' in the native struct.
            // Write playback/capture format and channels at their actual native C offsets.
            PatchNativeDeviceConfig(configPtr, physicalOutChannels, _config.EnableInput, physicalInChannels);

            // Initialize device
            result = MaBinding.ma_device_init(_maContext, configPtr, _maDevice);
            MaBinding.ma_free(configPtr, IntPtr.Zero, "Device config cleanup");

            if (playbackDeviceIdPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(playbackDeviceIdPtr);
            if (captureDeviceIdPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(captureDeviceIdPtr);

            if (result != MaResult.Success)
            {
                Log.Warning($"MiniAudio device init failed ({result}). Retrying with default devices...");

                IntPtr fallbackConfigPtr = MaBinding.allocate_device_config(
                    deviceType, MaFormat.F32, physicalOutChannels,
                    (uint)_config.SampleRate, _maCallback,
                    IntPtr.Zero, IntPtr.Zero, (uint)_config.BufferSize);

                if (fallbackConfigPtr != IntPtr.Zero)
                {
                    PatchNativeDeviceConfig(fallbackConfigPtr, physicalOutChannels, _config.EnableInput, physicalInChannels);
                    result = MaBinding.ma_device_init(_maContext, fallbackConfigPtr, _maDevice);
                    MaBinding.ma_free(fallbackConfigPtr, IntPtr.Zero, "Fallback device config cleanup");
                }

                if (result == MaResult.Success)
                {
                    _config.OutputDeviceId = null;
                    _config.InputDeviceId = null;
                    Log.Info("MiniAudio initialized with default devices (fallback)");
                }
            }

            if (result != MaResult.Success)
            {
                MaBinding.ma_free(_maDevice, IntPtr.Zero, "Device init failed");
                MaBinding.ma_context_uninit(_maContext);
                MaBinding.ma_free(_maContext, IntPtr.Zero, "Context cleanup after device init failure");
                _maContext = IntPtr.Zero;
                _maDevice = IntPtr.Zero;
                return (int)result;
            }

            // Initialize ring buffers with LOGICAL channel count (application-facing)
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
                int logicalChannels = _config.Channels;
                int physOutChannels = _physicalOutputChannels;
                int physInChannels = _physicalInputChannels;
                bool useOutputRouting = _config.OutputChannelSelectors != null && _config.OutputChannelSelectors.Length > 0
                                        && physOutChannels != logicalChannels;
                bool useInputRouting = _config.InputChannelSelectors != null && _config.InputChannelSelectors.Length > 0
                                        && physInChannels != logicalChannels;

                int logicalSampleCount = (int)frameCount * logicalChannels;
                int physicalSampleCount = (int)frameCount * physOutChannels;

                // Handle output (playback)
                if (pOutput != null)
                {
                    // Check if we're in pre-buffering state
                    if (_isBuffering == 1)
                    {
                        int availableSamples = _outputRing.AvailableRead;
                        if (availableSamples >= _prebufferThreshold)
                        {
                            _isBuffering = 0;
                        }
                        else
                        {
                            // Still buffering - output silence
                            new Span<float>(pOutput, physicalSampleCount).Clear();

                            if (_config.EnableInput && pInput != null)
                            {
                                if (useInputRouting)
                                {
                                    Span<float> logIn = stackalloc float[logicalSampleCount];
                                    RouteInputChannels(pInput, logIn, (int)frameCount, physInChannels, logicalChannels, _config.InputChannelSelectors!);
                                    _inputRing.Write(logIn);
                                }
                                else
                                {
                                    _inputRing.Write(new Span<float>(pInput, logicalSampleCount));
                                }
                            }
                            return;
                        }
                    }

                    if (useOutputRouting)
                    {
                        // Read logical samples into a temp buffer, then route to physical channels
                        Span<float> logicalBuf = stackalloc float[logicalSampleCount];
                        int samplesRead = _outputRing.Read(logicalBuf);

                        if (samplesRead > 0)
                        {
                            if (samplesRead < logicalSampleCount)
                                logicalBuf.Slice(samplesRead).Clear();

                            RouteOutputChannels((float*)pOutput, logicalBuf, (int)frameCount,
                                physOutChannels, logicalChannels, _config.OutputChannelSelectors!);
                            _isActive = 1;
                        }
                        else
                        {
                            new Span<float>(pOutput, physicalSampleCount).Clear();
                        }
                    }
                    else
                    {
                        // No routing - direct path
                        Span<float> outputSpan = new Span<float>(pOutput, logicalSampleCount);
                        int samplesRead = _outputRing.Read(outputSpan);

                        if (samplesRead > 0)
                        {
                            if (samplesRead < logicalSampleCount)
                                outputSpan.Slice(samplesRead).Clear();
                            _isActive = 1;
                        }
                        else
                        {
                            outputSpan.Clear();
                        }
                    }
                }

                // Handle input (recording)
                if (_config.EnableInput)
                {
                    if (pInput == null)
                    {
                        if (Interlocked.CompareExchange(ref _inputDiagLogged, 1, 0) == 0)
                            Log.Warning("MiniAudio: pInput is NULL — duplex capture not delivering data. Check macOS microphone permission (System Settings → Privacy → Microphone).");
                    }
                    else
                    {
                        if (Interlocked.CompareExchange(ref _inputDiagLogged, 1, 0) == 0)
                            Log.Info("MiniAudio: pInput is non-null — capture data is flowing from microphone.");

                        var inputSpan = new Span<float>(pInput, logicalSampleCount);

                        // One-time check: warn if all samples are zero (e.g. permission denied silently)
                        if (Interlocked.CompareExchange(ref _inputSilenceLogged, 1, 0) == 0)
                        {
                            bool allZero = true;
                            for (int i = 0; i < inputSpan.Length && allZero; i++)
                                if (inputSpan[i] != 0f) allZero = false;
                            if (allZero)
                                Log.Warning("MiniAudio: First capture buffer is all zeros — microphone may be muted or permission denied (macOS: System Settings → Privacy → Microphone → Terminal).");
                            else
                                Log.Info("MiniAudio: First capture buffer contains non-zero audio data — microphone is active.");
                        }

                        if (useInputRouting)
                        {
                            Span<float> logIn = stackalloc float[logicalSampleCount];
                            RouteInputChannels(pInput, logIn, (int)frameCount, physInChannels, logicalChannels, _config.InputChannelSelectors!);
                            _inputRing.Write(logIn);
                        }
                        else
                        {
                            _inputRing.Write(inputSpan);
                        }
                    }
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

            // Ring buffers use LOGICAL channel count (application-facing)
            int ringBufferSize = _config.BufferSize * _config.Channels * 4; // 4x buffer
            if (_outputRing == null || _outputRing.Capacity != ringBufferSize)
                _outputRing = new LockFreeRingBuffer<float>(ringBufferSize);

            if (_config.EnableInput && (_inputRing == null || _inputRing.Capacity != ringBufferSize))
                _inputRing = new LockFreeRingBuffer<float>(ringBufferSize);

            // Create device config with physical channel count
            MaDeviceType deviceType = _config.EnableInput ? MaDeviceType.Duplex : MaDeviceType.Playback;
            _maCallback = MiniAudioCallback;
            _maCallbackHandle = GCHandle.Alloc(_maCallback);

            uint physicalOutChannels = (uint)_physicalOutputChannels;

            ResolveMiniAudioDeviceIds(out IntPtr playbackDeviceIdPtr, out IntPtr captureDeviceIdPtr);

            IntPtr configPtr = MaBinding.allocate_device_config(
                deviceType,
                MaFormat.F32,
                physicalOutChannels,
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

            PatchNativeDeviceConfig(configPtr, physicalOutChannels, _config.EnableInput, (uint)_physicalInputChannels);

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
                Log.Warning($"MiniAudio device init failed with error {result} ({(int)result}). Retrying with default devices...");

                // Retry with default devices — specific device IDs may be invalid on some platforms
                // (e.g. macOS CoreAudio duplex with separate speaker/microphone devices).
                IntPtr fallbackConfigPtr = MaBinding.allocate_device_config(
                    deviceType,
                    MaFormat.F32,
                    physicalOutChannels,
                    (uint)_config.SampleRate,
                    _maCallback,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    (uint)_config.BufferSize
                );

                if (fallbackConfigPtr != IntPtr.Zero)
                {
                    PatchNativeDeviceConfig(fallbackConfigPtr, physicalOutChannels, _config.EnableInput, (uint)_physicalInputChannels);
                    result = MaBinding.ma_device_init(_maContext, fallbackConfigPtr, _maDevice);
                    MaBinding.ma_free(fallbackConfigPtr, IntPtr.Zero, "Fallback device config cleanup");
                }

                if (result != MaResult.Success)
                {
                    MaBinding.ma_free(_maDevice, IntPtr.Zero, "Device init failed");
                    _maDevice = IntPtr.Zero;
                    Log.Error($"MiniAudio device reinitialization failed: {result} ({(int)result})");
                    return (int)result;
                }

                // Clear persisted device IDs so future reinits also use defaults
                _config.InputDeviceId = null;
                _config.OutputDeviceId = null;
                Log.Info("MiniAudio device reinitialized with default devices (fallback)");
            }
            else
            {
                Log.Info("MiniAudio device reinitialized successfully");
            }

            return 0;
        }

        /// <summary>
        /// Resolves the configured output and capture device IDs into unmanaged <c>MaDeviceId</c>
        /// pointers that can be passed directly to <c>allocate_device_config</c>.
        /// Both pointers default to <see cref="IntPtr.Zero"/> (platform default device) when the
        /// corresponding config property is absent, malformed, or refers to an out-of-range index.
        /// </summary>
        /// <param name="playbackDeviceIdPtr">
        /// On return: an <see cref="Marshal.AllocHGlobal(int)"/>-allocated pointer to the resolved
        /// playback <c>MaDeviceId</c>, or <see cref="IntPtr.Zero"/> when the default device is used.
        /// The caller is responsible for freeing this pointer with <see cref="Marshal.FreeHGlobal(IntPtr)"/>.
        /// </param>
        /// <param name="captureDeviceIdPtr">
        /// On return: an <see cref="Marshal.AllocHGlobal(int)"/>-allocated pointer to the resolved
        /// capture <c>MaDeviceId</c>, or <see cref="IntPtr.Zero"/> when the default device is used.
        /// The caller is responsible for freeing this pointer with <see cref="Marshal.FreeHGlobal(IntPtr)"/>.
        /// </param>
        private void ResolveMiniAudioDeviceIds(out IntPtr playbackDeviceIdPtr, out IntPtr captureDeviceIdPtr)
        {
            playbackDeviceIdPtr = IntPtr.Zero;
            captureDeviceIdPtr = IntPtr.Zero;

            if (!string.IsNullOrEmpty(_config.OutputDeviceId) && _config.OutputDeviceId.StartsWith("ma_output_"))
            {
                if (int.TryParse(_config.OutputDeviceId.Substring("ma_output_".Length), out int deviceIndex))
                {
                    MaResult enumResult = MaBinding.ma_context_get_devices(
                        _maContext,
                        out IntPtr pPlaybackDevices,
                        out _,
                        out int playbackCount,
                        out _);

                    if (enumResult == MaResult.Success && deviceIndex < playbackCount)
                    {
                        int deviceInfoSize = Marshal.SizeOf<MaDeviceInfo>();
                        IntPtr deviceInfoPtr = IntPtr.Add(pPlaybackDevices, deviceIndex * deviceInfoSize);
                        var deviceInfo = Marshal.PtrToStructure<MaDeviceInfo>(deviceInfoPtr);

                        playbackDeviceIdPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MaDeviceId>());
                        Marshal.StructureToPtr(deviceInfo.Id, playbackDeviceIdPtr, false);
                    }
                }
            }

            if (_config.EnableInput && !string.IsNullOrEmpty(_config.InputDeviceId) && _config.InputDeviceId.StartsWith("ma_input_"))
            {
                if (int.TryParse(_config.InputDeviceId.Substring("ma_input_".Length), out int deviceIndex))
                {
                    MaResult enumResult = MaBinding.ma_context_get_devices(
                        _maContext,
                        out _,
                        out IntPtr pCaptureDevices,
                        out _,
                        out int captureCount);

                    if (enumResult == MaResult.Success && deviceIndex < captureCount)
                    {
                        int deviceInfoSize = Marshal.SizeOf<MaDeviceInfo>();
                        IntPtr deviceInfoPtr = IntPtr.Add(pCaptureDevices, deviceIndex * deviceInfoSize);
                        var deviceInfo = Marshal.PtrToStructure<MaDeviceInfo>(deviceInfoPtr);

                        captureDeviceIdPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MaDeviceId>());
                        Marshal.StructureToPtr(deviceInfo.Id, captureDeviceIdPtr, false);
                    }
                }
            }
        }

        /// <summary>
        /// Patches a native ma_device_config buffer to write playback/capture format and channel
        /// count at the correct native C offsets.
        ///
        /// The managed MaResamplerConfig is 16 bytes larger than the actual native
        /// ma_resampler_config (48 bytes on 64-bit in MiniAudio 0.11.x).
        /// Marshal.StructureToPtr therefore writes playback/capture fields at wrong positions,
        /// so MiniAudio reads zeros for channels and defaults to device native values.
        ///
        /// Verified native field offsets (from ma_device_init disassembly on arm64):
        ///   playback.pDeviceID @ 112   playback.format @ 120   playback.channels @ 124
        ///   capture.pDeviceID  @ 152   capture.format  @ 160   capture.channels  @ 164
        /// </summary>
        private static void PatchNativeDeviceConfig(IntPtr configPtr, uint playbackChannels, bool hasCapture, uint captureChannels)
        {
            Marshal.WriteInt32(configPtr, 120, (int)MaFormat.F32);
            Marshal.WriteInt32(configPtr, 124, (int)playbackChannels);
            if (hasCapture)
            {
                Marshal.WriteInt32(configPtr, 160, (int)MaFormat.F32);
                Marshal.WriteInt32(configPtr, 164, (int)captureChannels);
            }
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

                    // Extract max channel counts and first non-zero native sample rate from nativeDataFormats
                    int maxOutputChannels = 0;
                    int maxInputChannels = 0;
                    double defaultSampleRate = 0;

                    if (deviceInfo.nativeDataFormats != null && deviceInfo.NativeDataFormatCount > 0)
                    {
                        for (int j = 0; j < deviceInfo.NativeDataFormatCount && j < deviceInfo.nativeDataFormats.Length; j++)
                        {
                            var format = deviceInfo.nativeDataFormats[j];
                            if (format.channels > maxOutputChannels)
                                maxOutputChannels = (int)format.channels;
                            if (defaultSampleRate == 0 && format.sampleRate > 0)
                                defaultSampleRate = format.sampleRate;
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
                        maxOutputChannels: maxOutputChannels,
                        defaultSampleRate: defaultSampleRate
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

                    // Extract max channel counts and first non-zero native sample rate from nativeDataFormats
                    int maxInputChannels = 0;
                    int maxOutputChannels = 0;
                    double defaultSampleRate = 0;

                    if (deviceInfo.nativeDataFormats != null && deviceInfo.NativeDataFormatCount > 0)
                    {
                        for (int j = 0; j < deviceInfo.NativeDataFormatCount && j < deviceInfo.nativeDataFormats.Length; j++)
                        {
                            var format = deviceInfo.nativeDataFormats[j];
                            if (format.channels > maxInputChannels)
                                maxInputChannels = (int)format.channels;
                            if (defaultSampleRate == 0 && format.sampleRate > 0)
                                defaultSampleRate = format.sampleRate;
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
                        maxOutputChannels: maxOutputChannels,
                        defaultSampleRate: defaultSampleRate
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
