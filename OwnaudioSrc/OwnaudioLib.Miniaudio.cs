using Ownaudio.Bindings.Miniaudio;
using Ownaudio.Engines;
using Ownaudio.Exceptions;
using Ownaudio.MiniAudio;
using Ownaudio.Utilities;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using static Ownaudio.Bindings.Miniaudio.MaBinding;

namespace Ownaudio;

/// <summary>
/// Functions to retrieve, configure and manage the current Ownaudio environment, 
/// which affects the entire directory configuration.
/// </summary>
public static partial class OwnAudio
{
    /// <summary>
    /// Terminates processes and frees memory.
    /// </summary>
    public static void FreeMiniAudio() 
    { 
        
    }

    /// <summary>
    /// Initialize and register MiniAudio functions by providing the path to MiniAudio's native library. 
    /// Leave the path parameter blank to use the system directory. 
    /// Exits if already initialized.
    /// </summary>
    /// <param name="miniAudioPath">Path to native miniaudio directory, eg miniaudio.dll, libminiaudio.so, libminiaudio.dylib.</param>
    /// <param name="hostType">Audio API type</param>
    /// <exception cref="OwnaudioException">Throws an exception if no output device is available.</exception>
    private static void InitializeMiniAudio(string? miniAudioPath = default, OwnAudioEngine.EngineHostType hostType = OwnAudioEngine.EngineHostType.None)
    {
        if (IsMiniAudioInitialized || string.IsNullOrEmpty(miniAudioPath))
        {
            return;
        }

        IsMiniAudioInitialized = false;

        try
        {
            MaBinding.InitializeBindings(new LibraryLoader(miniAudioPath));
            //MaBinding.InitializeBindings(miniAudioPath);
            _outputDevices.Clear();
            _inputDevices.Clear();

            var engine = new MiniAudioEngine();
            engine.UpdateDevicesInfo();

            _outputDevices = engine.PlaybackDevices
                .Select((dev, index) => new AudioDevice(index, dev.Name, GetDeviceChannelCount(engine, index, false), 0, 0.02, 0, 0, 0, GetDeviceSampleRate(engine, index, false)))
                .ToList();

            _inputDevices = engine.CaptureDevices
                .Select((dev, index) => new AudioDevice(index, dev.Name, 0, GetDeviceChannelCount(engine, index, true), 0, 0, 0.02, 0, GetDeviceSampleRate(engine, index, true)))
                .ToList();

            if (_outputDevices.Count > 0)
            {
                IsMiniAudioInitialized = true;
                _defaultOutputDevice = _outputDevices[0];
            }

            if (_inputDevices.Count > 0)
            {
                _defaultInputDevice = _inputDevices[0];
            }

            MiniAudioPath = miniAudioPath;
        }
        catch (Exception)
        {
            Debug.WriteLine("Miniaudio initialize error.");
        }  
    }

    /// <summary>
    /// Determines the preferred sample rate for a specific audio device by testing common sample rates.
    /// </summary>
    /// <param name="engine">The MiniAudio engine instance used for device testing.</param>
    /// <param name="deviceIndex">The zero-based index of the device in the engine's device list.</param>
    /// <param name="isInput">True if testing an input (capture) device; false for output (playback) device.</param>
    /// <returns>
    /// The highest supported sample rate from the common rates list (48000, 44100, 96000, etc.), 
    /// or 44100 Hz as a fallback if no rates can be determined or an error occurs.
    /// </returns>
    /// <remarks>
    /// This method tests sample rates in order of preference: 48kHz, 44.1kHz, 96kHz, 88.2kHz, 192kHz, 32kHz, 22.05kHz, and 16kHz.
    /// For each rate, it attempts to initialize a test device configuration to verify hardware support.
    /// The first successfully supported rate is returned immediately, making this method efficient for most devices.
    /// 
    /// Note: This method performs actual device initialization tests, which may have a performance impact.
    /// Consider caching results for frequently accessed devices.
    /// </remarks>
    /// <exception cref="Exception">
    /// Catches and logs any exceptions during device testing, returning the default fallback rate.
    /// </exception>
    private static int GetDeviceSampleRate(MiniAudioEngine engine, int deviceIndex, bool isInput)
    {
        try
        {
            int[] commonSampleRates = { 48000, 44100, 96000, 88200, 192000, 32000, 22050, 16000 };

            var devices = isInput ? engine.CaptureDevices : engine.PlaybackDevices;
            if (deviceIndex >= devices.Count)
                return 44100;

            var device = devices[deviceIndex];

            foreach (int sampleRate in commonSampleRates)
            {
                try
                {
                    var deviceType = isInput ? MaDeviceType.Capture : MaDeviceType.Playback;
                    var config = MaBinding.ma_device_config_init(deviceType);
                    config.sampleRate = (uint)sampleRate;
                    config.playback.format = MaFormat.F32;
                    config.capture.format = MaFormat.F32;
                    config.playback.channels = 2;
                    config.capture.channels = 2;

                    IntPtr testDevice = MaBinding.allocate_device();
                    if (testDevice != IntPtr.Zero)
                    {
                        try
                        {
                            IntPtr configPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MaDeviceConfig>());
                            try
                            {
                                Marshal.StructureToPtr(config, configPtr, false);
                                var result = MaBinding.ma_device_init(IntPtr.Zero, configPtr, testDevice);

                                if (result == MaResult.Success)
                                {
                                    MaBinding.ma_device_uninit(testDevice);
                                    return sampleRate; 
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(configPtr);
                            }
                        }
                        finally
                        {
                            MaBinding.ma_free(testDevice, IntPtr.Zero, "Test device cleanup");
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error testing device sample rates: {ex.Message}");
        }

        return 44100;
    }

    /// <summary>
    /// Determines the maximum supported channel count for a specific audio device by testing common channel configurations.
    /// </summary>
    /// <param name="engine">The MiniAudio engine instance used for device testing.</param>
    /// <param name="deviceIndex">The zero-based index of the device in the engine's device list.</param>
    /// <param name="isInput">True if testing an input (capture) device; false for output (playback) device.</param>
    /// <returns>
    /// The highest supported channel count from the common configurations (8, 6, 4, 2, 1), 
    /// or 2 as a fallback if no channel count can be determined or an error occurs.
    /// </returns>
    /// <remarks>
    /// This method tests channel counts in descending order: 8, 6, 4, 2, 1 channels.
    /// For each count, it attempts to initialize a test device configuration to verify hardware support.
    /// The first successfully supported channel count is returned immediately.
    /// 
    /// Note: This method performs actual device initialization tests, which may have a performance impact.
    /// Consider caching results for frequently accessed devices.
    /// </remarks>
    private static int GetDeviceChannelCount(MiniAudioEngine engine, int deviceIndex, bool isInput)
    {
        try
        {
            int[] commonChannelCounts = { 8, 6, 4, 2, 1 };

            var devices = isInput ? engine.CaptureDevices : engine.PlaybackDevices;
            if (deviceIndex >= devices.Count)
                return 2;

            var device = devices[deviceIndex];

            foreach (int channelCount in commonChannelCounts)
            {
                try
                {
                    var deviceType = isInput ? MaDeviceType.Capture : MaDeviceType.Playback;
                    var config = MaBinding.ma_device_config_init(deviceType);
                    config.sampleRate = 44100; // Use standard sample rate for testing
                    config.playback.format = MaFormat.F32;
                    config.capture.format = MaFormat.F32;
                    config.playback.channels = (uint)channelCount;
                    config.capture.channels = (uint)channelCount;

                    IntPtr testDevice = MaBinding.allocate_device();
                    if (testDevice != IntPtr.Zero)
                    {
                        try
                        {
                            IntPtr configPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MaDeviceConfig>());
                            try
                            {
                                Marshal.StructureToPtr(config, configPtr, false);
                                var result = MaBinding.ma_device_init(IntPtr.Zero, configPtr, testDevice);

                                if (result == MaResult.Success)
                                {
                                    MaBinding.ma_device_uninit(testDevice);
                                    return channelCount;
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(configPtr);
                            }
                        }
                        finally
                        {
                            MaBinding.ma_free(testDevice, IntPtr.Zero, "Test device cleanup");
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error testing device channel counts: {ex.Message}");
        }

        return 2; // Default stereo fallback
    }
}
