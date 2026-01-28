using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Logger;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Native.Utils;
using Ownaudio.Native.PortAudio;
using static Ownaudio.Native.PortAudio.PaBinding;

namespace Ownaudio.Native
{
    public sealed partial class NativeAudioEngine
    {
        #region PortAudio Fields

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

        #endregion

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
                Log.Info($"PortAudio: Requesting host API '{_config.HostType}'");
            }
            else
            {
                Log.Info("PortAudio: Using default host API");
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
                    Log.Info($"PortAudio: Using host API '{hostApiInfo.type}' with device '{deviceInfo.name}'");
                }
            }

            // Check if we need ASIO-specific configuration
            bool useAsioChannelSelectors = _selectedHostApiType == PaHostApiTypeId.paASIO &&
                                           (_config.OutputChannelSelectors != null && _config.OutputChannelSelectors.Length > 0);

            // Prepare ASIO stream info for output if needed
            PaAsioStreamInfo outputAsioInfo = default;
            IntPtr outputAsioInfoPtr = IntPtr.Zero;

            if (useAsioChannelSelectors)
            {
                (outputAsioInfo, outputAsioInfoPtr) = CreateAsioOutputStreamInfo(_config.OutputChannelSelectors!);
            }

            var outputParams = new PaStreamParameters
            {
                device = _activeOutputDeviceIndex,
                channelCount = _config.Channels,
                sampleFormat = PaSampleFormat.paFloat32,
                suggestedLatency = outputLatency,
                hostApiSpecificStreamInfo = outputAsioInfoPtr
            };

            IntPtr inputParamsPtr = IntPtr.Zero;
            PaAsioStreamInfo inputAsioInfo = default;
            IntPtr inputAsioInfoPtr = IntPtr.Zero;

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

                // Check if we need ASIO-specific configuration for input
                bool useAsioInputChannelSelectors = _selectedHostApiType == PaHostApiTypeId.paASIO &&
                                                    (_config.InputChannelSelectors != null && _config.InputChannelSelectors.Length > 0);

                if (useAsioInputChannelSelectors)
                {
                    (inputAsioInfo, inputAsioInfoPtr) = CreateAsioInputStreamInfo(_config.InputChannelSelectors!);
                }

                var inputParams = new PaStreamParameters
                {
                    device = _activeInputDeviceIndex,
                    channelCount = _config.Channels,
                    sampleFormat = PaSampleFormat.paFloat32,
                    suggestedLatency = inputLatency,
                    hostApiSpecificStreamInfo = inputAsioInfoPtr
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

            // Cleanup allocated memory
            Marshal.FreeHGlobal(outputParamsPtr);
            if (inputParamsPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(inputParamsPtr);

            // Free ASIO info structures and their internal channel selector arrays
            FreeAsioStreamInfo(ref outputAsioInfo, outputAsioInfoPtr);
            FreeAsioStreamInfo(ref inputAsioInfo, inputAsioInfoPtr);

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
                            _inputRing.Write(inputSpan);
                        }

                        return PaStreamCallbackResult.paContinue;
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

                // Handle input if enabled - Zero-copy: write directly from input
                if (_config.EnableInput && input != null)
                {
                    Span<float> inputSpan = new Span<float>(input, sampleCount);
                    _inputRing.Write(inputSpan);
                }

                return PaStreamCallbackResult.paContinue;
            }
            catch
            {
                _isActive = -1;
                return PaStreamCallbackResult.paAbort;
            }
        }

        /// <summary>
        /// Gets output devices using PortAudio backend.
        /// </summary>
        /// <returns>List of output device information.</returns>
        private List<AudioDeviceInfo> GetPortAudioOutputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

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

                    // Determine final channel counts (probe for ASIO)
                    int finalMaxInput = paDeviceInfo.maxInputChannels;
                    int finalMaxOutput = paDeviceInfo.maxOutputChannels;

                    if (selectedHostApiIndex >= 0 &&
                        paDeviceInfo.hostApi == selectedHostApiIndex &&
                        _selectedHostApiType == PaHostApiTypeId.paASIO)
                    {
                        Log.Info($"Probing ASIO device (Output enumeration): {paDeviceInfo.name}");
                        var probed = ProbeAsioDeviceChannels(i, paDeviceInfo);
                        finalMaxInput = probed.maxInput;
                        finalMaxOutput = probed.maxOutput;
                        Log.Info($"ASIO device '{paDeviceInfo.name}' probed: {finalMaxOutput} outputs, {finalMaxInput} inputs");
                    }

                    if (finalMaxOutput > 0)
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
                            isInput: finalMaxInput > 0,
                            isOutput: true,
                            isDefault: (i == defaultDeviceIndex),
                            state: AudioDeviceState.Active,
                            maxInputChannels: finalMaxInput,
                            maxOutputChannels: finalMaxOutput
                        ));
                    }
                }
            }

            return devices;
        }

        /// <summary>
        /// Gets input devices using PortAudio backend.
        /// </summary>
        /// <returns>List of input device information.</returns>
        private List<AudioDeviceInfo> GetPortAudioInputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

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

                    // Determine final channel counts (probe for ASIO)
                    int finalMaxInput = paDeviceInfo.maxInputChannels;
                    int finalMaxOutput = paDeviceInfo.maxOutputChannels;

                    if (selectedHostApiIndex >= 0 &&
                        paDeviceInfo.hostApi == selectedHostApiIndex &&
                        _selectedHostApiType == PaHostApiTypeId.paASIO)
                    {
                        Log.Info($"Probing ASIO device (Input enumeration): {paDeviceInfo.name}");
                        var probed = ProbeAsioDeviceChannels(i, paDeviceInfo);
                        finalMaxInput = probed.maxInput;
                        finalMaxOutput = probed.maxOutput;
                        Log.Info($"ASIO device '{paDeviceInfo.name}' probed: {finalMaxOutput} outputs, {finalMaxInput} inputs");
                    }

                    if (finalMaxInput > 0)
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
                            isOutput: finalMaxOutput > 0,
                            isDefault: (i == defaultDeviceIndex),
                            state: AudioDeviceState.Active,
                            maxInputChannels: finalMaxInput,
                            maxOutputChannels: finalMaxOutput
                        ));
                    }
                }
            }

            return devices;
        }

        /// <summary>
        /// Gets the raw device count from PortAudio backend.
        /// </summary>
        /// <returns>Total number of devices.</returns>
        private int GetPortAudioRawDeviceCount()
        {
            return Pa_GetDeviceCount();
        }

        /// <summary>
        /// Starts PortAudio stream.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private int StartPortAudio()
        {
            return Pa_StartStream(_paStream);
        }

        /// <summary>
        /// Stops PortAudio stream.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        private int StopPortAudio()
        {
            return Pa_StopStream(_paStream);
        }

        /// <summary>
        /// Disposes PortAudio resources.
        /// </summary>
        private void DisposePortAudio()
        {
            if (_paStream != IntPtr.Zero)
            {
                Pa_CloseStream(_paStream);
                _paStream = IntPtr.Zero;
            }
            Pa_Terminate();

            if (_callbackHandle.IsAllocated)
                _callbackHandle.Free();

            _portAudioLoader?.Dispose();
        }

        #endregion
    }
}
