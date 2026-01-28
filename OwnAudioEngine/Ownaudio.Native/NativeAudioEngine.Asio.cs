using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Logger;
using Ownaudio.Native.PortAudio;
using static Ownaudio.Native.PortAudio.PaBinding;

namespace Ownaudio.Native
{
    public sealed partial class NativeAudioEngine
    {
        #region ASIO Fields

        /// <summary>
        /// Cache for ASIO device channel counts to avoid expensive re-probing during periodic checks.
        /// Key: Device Name, Value: (MaxInput, MaxOutput)
        /// </summary>
        private readonly Dictionary<string, (int maxInput, int maxOutput)> _asioChannelCache = new();

        #endregion

        #region ASIO Implementation

        /// <summary>
        /// Probes an ASIO device to determine the exact number of supported input and output channels.
        /// This method tests the device with incrementing channel counts using Pa_IsFormatSupported.
        /// </summary>
        /// <param name="deviceIndex">The global PortAudio device index to probe.</param>
        /// <param name="deviceInfo">The device info structure for the device.</param>
        /// <returns>A tuple containing (maxInputChannels, maxOutputChannels).</returns>
        private unsafe (int maxInput, int maxOutput) ProbeAsioDeviceChannels(int deviceIndex, PaDeviceInfo deviceInfo)
        {
            // Check cache first
            if (_asioChannelCache.TryGetValue(deviceInfo.name, out var cached))
            {
                return cached;
            }

            int maxOutputChannels = 0;
            int maxInputChannels = 0;

            try
            {
                // Output channels testing
                // Test up to 32 channels (common maximum for ASIO devices)
                for (int ch = 1; ch <= 32; ch++)
                {
                    var testParams = new PaStreamParameters
                    {
                        device = deviceIndex,
                        channelCount = ch,
                        sampleFormat = PaSampleFormat.paFloat32,
                        suggestedLatency = deviceInfo.defaultLowOutputLatency > 0
                            ? deviceInfo.defaultLowOutputLatency
                            : 0.01, // 10ms fallback
                        hostApiSpecificStreamInfo = IntPtr.Zero
                    };

                    IntPtr testParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(testParams));
                    Marshal.StructureToPtr(testParams, testParamsPtr, false);

                    // Test output: inputParameters = null, outputParameters = testParams
                    int result = Pa_IsFormatSupported(IntPtr.Zero, testParamsPtr, 48000.0);
                    Marshal.FreeHGlobal(testParamsPtr);

                    if (result == 0)
                    {
                        // Format is supported
                        maxOutputChannels = ch;
                    }
                    else
                    {
                        // Format not supported, we've found the maximum
                        break;
                    }
                }

                // Input channels testing
                for (int ch = 1; ch <= 32; ch++)
                {
                    var testParams = new PaStreamParameters
                    {
                        device = deviceIndex,
                        channelCount = ch,
                        sampleFormat = PaSampleFormat.paFloat32,
                        suggestedLatency = deviceInfo.defaultLowInputLatency > 0
                            ? deviceInfo.defaultLowInputLatency
                            : 0.01, // 10ms fallback
                        hostApiSpecificStreamInfo = IntPtr.Zero
                    };

                    IntPtr testParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(testParams));
                    Marshal.StructureToPtr(testParams, testParamsPtr, false);

                    // Test input: inputParameters = testParams, outputParameters = null
                    int result = Pa_IsFormatSupported(testParamsPtr, IntPtr.Zero, 48000.0);
                    Marshal.FreeHGlobal(testParamsPtr);

                    if (result == 0)
                    {
                        // Format is supported
                        maxInputChannels = ch;
                    }
                    else
                    {
                        // Format not supported, we've found the maximum
                        break;
                    }
                }

                Log.Info($"ASIO device probe complete: {maxOutputChannels} output, {maxInputChannels} input channels");

                // Cache the result
                _asioChannelCache[deviceInfo.name] = (maxInputChannels, maxOutputChannels);
            }
            catch (Exception ex)
            {
                Log.Warning($"ASIO device probing failed for device index {deviceIndex}: {ex.Message}");
                // Return fallback values based on deviceInfo
                maxOutputChannels = Math.Max(deviceInfo.maxOutputChannels, 2);
                maxInputChannels = Math.Max(deviceInfo.maxInputChannels, 0);
            }

            return (maxInputChannels, maxOutputChannels);
        }

        /// <summary>
        /// Creates ASIO stream info for output with custom channel selectors.
        /// </summary>
        /// <param name="channelSelectors">The channel selectors to use.</param>
        /// <returns>A tuple containing the ASIO stream info and its allocated pointer.</returns>
        private (PaAsioStreamInfo info, IntPtr ptr) CreateAsioOutputStreamInfo(int[] channelSelectors)
        {
            var asioInfo = new PaAsioStreamInfo
            {
                size = (uint)Marshal.SizeOf<PaAsioStreamInfo>(),
                hostApiType = PaHostApiTypeId.paASIO,
                version = 1,
                flags = (uint)PaAsioFlags.UseChannelSelectors
            };
            asioInfo.SetChannelSelectors(channelSelectors);

            IntPtr asioInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PaAsioStreamInfo>());
            Marshal.StructureToPtr(asioInfo, asioInfoPtr, false);

            Log.Info($"PortAudio ASIO: Using custom output channel selectors: [{string.Join(", ", channelSelectors)}]");

            return (asioInfo, asioInfoPtr);
        }

        /// <summary>
        /// Creates ASIO stream info for input with custom channel selectors.
        /// </summary>
        /// <param name="channelSelectors">The channel selectors to use.</param>
        /// <returns>A tuple containing the ASIO stream info and its allocated pointer.</returns>
        private (PaAsioStreamInfo info, IntPtr ptr) CreateAsioInputStreamInfo(int[] channelSelectors)
        {
            var asioInfo = new PaAsioStreamInfo
            {
                size = (uint)Marshal.SizeOf<PaAsioStreamInfo>(),
                hostApiType = PaHostApiTypeId.paASIO,
                version = 1,
                flags = (uint)PaAsioFlags.UseChannelSelectors
            };
            asioInfo.SetChannelSelectors(channelSelectors);

            IntPtr asioInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PaAsioStreamInfo>());
            Marshal.StructureToPtr(asioInfo, asioInfoPtr, false);

            Log.Info($"PortAudio ASIO: Using custom input channel selectors: [{string.Join(", ", channelSelectors)}]");

            return (asioInfo, asioInfoPtr);
        }

        /// <summary>
        /// Frees ASIO stream info resources.
        /// </summary>
        /// <param name="asioInfo">The ASIO stream info to free.</param>
        /// <param name="asioInfoPtr">The allocated pointer to free.</param>
        private void FreeAsioStreamInfo(ref PaAsioStreamInfo asioInfo, IntPtr asioInfoPtr)
        {
            if (asioInfoPtr != IntPtr.Zero)
            {
                asioInfo.Free();
                Marshal.FreeHGlobal(asioInfoPtr);
            }
        }

        #endregion
    }
}
