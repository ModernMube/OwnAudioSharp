using System;
using System.Collections.Generic;

using Ownaudio.Bindings.PortAudio;
using Ownaudio.Exceptions;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using Ownaudio.Engines;
using System.IO;
using System.Diagnostics;
using Avalonia.Logging;

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
    public static void Free()
    {
        if(IsPortAudioInitialized)
        {
            PaBinding.Pa_Terminate();
            IsPortAudioInitialized = false;
        }   
        
        if(IsMiniAudioInitialized)
            IsMiniAudioInitialized = false;
    }

    /// <summary>
    /// Initialize and register PortAudio functions by providing the path to PortAudio's native library. 
    /// Leave the path parameter blank to use the system directory. 
    /// Exits if already initialized.
    /// </summary>
    /// <param name="portAudioPath">Path to native port audio directory, eg portaudio.dll, libportaudio.so, libportaudio.dylib.</param>
    /// <param name="hostType">Audio API type</param>
    /// <exception cref="OwnaudioException">Throws an exception if no output device is available.</exception>
    private static void InitializePortAudio(string? portAudioPath = default, OwnAudioEngine.EngineHostType hostType = OwnAudioEngine.EngineHostType.None)
    {
        if (IsPortAudioInitialized || string.IsNullOrEmpty(portAudioPath))
        {
            return;
        }

        IsPortAudioInitialized = false;

        try
        {
            PaBinding.InitializeBindings(new LibraryLoader(portAudioPath));
            PaBinding.Pa_Initialize().PaGuard();

            if (hostType == OwnAudioEngine.EngineHostType.None)
            {
                HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paWDMKS);

                if (Utilities.PlatformInfo.IsWindows)
                    HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paWASAPI);
                else if (Utilities.PlatformInfo.IsLinux)
                    HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paALSA);
                else if (Utilities.PlatformInfo.IsOSX)
                    HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paCoreAudio);
                else
                    HostID = PaBinding.Pa_GetDefaultHostApi();
            }
            else if (hostType == OwnAudioEngine.EngineHostType.ASIO)
            {
                HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paASIO).PaGuard();
            }
            else if (hostType == OwnAudioEngine.EngineHostType.COREAUDIO)
            {
                HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paCoreAudio).PaGuard();
            }
            else if (hostType == OwnAudioEngine.EngineHostType.ALSA)
            {
                HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paALSA).PaGuard();
            }
            else if (hostType == OwnAudioEngine.EngineHostType.WDMKS)
            {
                HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paWDMKS).PaGuard();
            }
            else if (hostType == OwnAudioEngine.EngineHostType.JACK)
            {
                HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paJACK).PaGuard();
            }
            else if (hostType == OwnAudioEngine.EngineHostType.WASAPI)
            {
                HostID = PaBinding.Pa_HostApiTypeIdToHostApiIndex(PaBinding.PaHostApiTypeId.paWASAPI).PaGuard();
            }

            int deviceCount = HostID.PaHostApiInfo().deviceCount;
            Ensure.That<OwnaudioException>(deviceCount > 0, "No output devices are available.");

            int defaultOutDevice = HostID.PaHostApiInfo().defaultOutputDevice;
            _defaultOutputDevice = defaultOutDevice.PaGetPaDeviceInfo().PaToAudioDevice(defaultOutDevice);
            _outputDevices = new List<AudioDevice>();

            int defaultInDevice = HostID.PaHostApiInfo().defaultInputDevice;
            if (defaultInDevice >= 0)
                _defaultInputDevice = defaultInDevice.PaGetPaDeviceInfo().PaToAudioDevice(defaultInDevice);
            _inputDevices = new List<AudioDevice>();

            for (var i = 0; i < deviceCount; i++)
            {
                int deviceIndex = PaBinding.Pa_HostApiDeviceIndexToDeviceIndex(HostID, i);
                var deviceInfo = deviceIndex.PaGetPaDeviceInfo();

                if (deviceInfo.maxOutputChannels > 0)
                    _outputDevices.Add(deviceInfo.PaToAudioDevice(i));

                if (deviceInfo.maxInputChannels > 0)
                    _inputDevices.Add(deviceInfo.PaToAudioDevice(i));
            }

            IsPortAudioInitialized = true;
        }
        catch (Exception)
        { 
            Debug.WriteLine($"Portaudio is not initialized.");
        }
    }
}
