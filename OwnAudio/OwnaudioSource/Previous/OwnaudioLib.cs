using Ownaudio.Core;
using OwnaudioLegacy.Exceptions;
#if WINDOWS
using Ownaudio.Windows;
#elif MACOS
using Ownaudio.macOS;
#elif LINUX
using Ownaudio.Linux;
#endif
using System;
using System.Threading;

namespace OwnaudioLegacy;
/// <summary>
/// Functions to retrieve, configure and manage the current Ownaudio environment,
/// which affects the entire directory configuration.
/// </summary>
[Obsolete("This is legacy code, available only for compatibility!")]
public static partial class OwnAudioEngine
{

    /// <summary>
    /// Initialize and register the PortAudio library and initialize and 
    /// register the FFmpeg functions with the FFmpeg native libraries, 
    /// the system default directory.
    /// </summary>
    public static bool Initialize()
    {
        IsInitialized = false;

        IsInitialized = audioEngineInitialize();

        return IsInitialized;          
    }

    /// <summary>
    /// Frees up the audio engine and decoder
    /// </summary>
    public static void Free()
    {
        try
        {
            // Stop SourceManager singleton instance if it exists
            try
            {
                Sources.SourceManager.Instance?.Stop();
            }
            catch { /* Ignore if SourceManager is not initialized */ }

            if(Engine?.OwnAudioEngineActivate() == 0)
            {
                // Cleanup
                Decoder?.Dispose();
                Engine?.Dispose();
            }
        }
        finally
        {
            // Always reset state
            IsInitialized = false;
            Engine = null;
            Decoder = null;
            _defaultOutputDevice = null;
            _defaultInputDevice = null;
            _outputDevices.Clear();
            _inputDevices.Clear();
        }
    }

    private static bool audioEngineInitialize()
    {
        IsInitialized = false;

        try
        {
            Engine = AudioEngineFactory.CreateDefault(); 

            if (Engine != null)
            {
                IsInitialized = true;

                getAllDevices();

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            throw new OwnaudioException($"Engine initialize error: {ex.Message}");
        }
    }

    private static void getAllDevices()
    {
        IDeviceEnumerator? enumerator = null;

#if WINDOWS
        enumerator = new WasapiDeviceEnumerator();
#elif MACOS
        enumerator = new CoreAudioDeviceEnumerator();
#elif LINUX
        enumerator = new PulseAudioDeviceEnumerator();
#endif


        if (enumerator != null && enumerator.EnumerateAllDevices().Count > 0)
        {
            _outputDevices = enumerator.EnumerateOutputDevices();
            _inputDevices = enumerator.EnumerateInputDevices();
            _defaultOutputDevice = enumerator.GetDefaultOutputDevice();
            _defaultInputDevice = enumerator.GetDefaultInputDevice();
        }
    }

    /// <summary>
    /// Refreshes the device lists by re-enumerating all available audio devices.
    /// </summary>
    /// <exception cref="OwnaudioException">Exception if audio engine is not initialized.</exception>
    public static void RefreshDevices()
    {
        if (!IsInitialized)
            throw new OwnaudioException("Audio engine is not initialized.");

        getAllDevices();
    }

    /// <summary>
    /// Changes the output device by device name.
    /// The engine must be stopped before changing devices.
    /// </summary>
    /// <param name="deviceName">The friendly name of the device.</param>
    /// <returns>True on success, false on failure.</returns>
    /// <exception cref="OwnaudioException">Exception if audio engine is not initialized.</exception>
    public static bool SetOutputDeviceByName(string deviceName)
    {
        if (!IsInitialized)
            throw new OwnaudioException("Audio engine is not initialized.");

        if (Engine == null)
            throw new OwnaudioException("Audio engine is null.");

        int result = Engine.SetOutputDeviceByName(deviceName);

        if (result == 0)
        {
            RefreshDevices();
            return true;
        }
        else if (result == -2)
        {
            throw new OwnaudioException("Cannot change device while engine is running. Stop the engine first.");
        }
        else if (result == -3)
        {
            throw new OwnaudioException($"Device '{deviceName}' not found.");
        }

        return false;
    }

    /// <summary>
    /// Changes the output device by index in the device list.
    /// The engine must be stopped before changing devices.
    /// </summary>
    /// <param name="deviceIndex">The zero-based index of the device in the output device list.</param>
    /// <returns>True on success, false on failure.</returns>
    /// <exception cref="OwnaudioException">Exception if audio engine is not initialized.</exception>
    public static bool SetOutputDeviceByIndex(int deviceIndex)
    {
        if (!IsInitialized)
            throw new OwnaudioException("Audio engine is not initialized.");

        if (Engine == null)
            throw new OwnaudioException("Audio engine is null.");

        if (deviceIndex < 0 || deviceIndex >= _outputDevices.Count)
            throw new OwnaudioException($"Device index {deviceIndex} is out of range (0-{_outputDevices.Count - 1}).");

        int result = Engine.SetOutputDeviceByIndex(deviceIndex);

        if (result == 0)
        {
            RefreshDevices();
            return true;
        }
        else if (result == -2)
        {
            throw new OwnaudioException("Cannot change device while engine is running. Stop the engine first.");
        }

        return false;
    }

    /// <summary>
    /// Changes the input device by device name.
    /// The engine must be stopped before changing devices.
    /// </summary>
    /// <param name="deviceName">The friendly name of the device.</param>
    /// <returns>True on success, false on failure.</returns>
    /// <exception cref="OwnaudioException">Exception if audio engine is not initialized.</exception>
    public static bool SetInputDeviceByName(string deviceName)
    {
        if (!IsInitialized)
            throw new OwnaudioException("Audio engine is not initialized.");

        if (Engine == null)
            throw new OwnaudioException("Audio engine is null.");

        int result = Engine.SetInputDeviceByName(deviceName);

        if (result == 0)
        {
            RefreshDevices();
            return true;
        }
        else if (result == -2)
        {
            throw new OwnaudioException("Cannot change device while engine is running. Stop the engine first.");
        }
        else if (result == -3)
        {
            throw new OwnaudioException($"Device '{deviceName}' not found.");
        }

        return false;
    }

    /// <summary>
    /// Changes the input device by index in the device list.
    /// The engine must be stopped before changing devices.
    /// </summary>
    /// <param name="deviceIndex">The zero-based index of the device in the input device list.</param>
    /// <returns>True on success, false on failure.</returns>
    /// <exception cref="OwnaudioException">Exception if audio engine is not initialized.</exception>
    public static bool SetInputDeviceByIndex(int deviceIndex)
    {
        if (!IsInitialized)
            throw new OwnaudioException("Audio engine is not initialized.");

        if (Engine == null)
            throw new OwnaudioException("Audio engine is null.");

        if (deviceIndex < 0 || deviceIndex >= _inputDevices.Count)
            throw new OwnaudioException($"Device index {deviceIndex} is out of range (0-{_inputDevices.Count - 1}).");

        int result = Engine.SetInputDeviceByIndex(deviceIndex);

        if (result == 0)
        {
            RefreshDevices();
            return true;
        }
        else if (result == -2)
        {
            throw new OwnaudioException("Cannot change device while engine is running. Stop the engine first.");
        }

        return false;
    }

    /// <summary>
    /// Gets information about a specific device by its index in the output device list.
    /// </summary>
    /// <param name="deviceIndex">The zero-based index of the device.</param>
    /// <returns>AudioDeviceInfo for the specified device.</returns>
    /// <exception cref="OwnaudioException">Exception if audio engine is not initialized or index is out of range.</exception>
    public static AudioDeviceInfo GetOutputDeviceInfo(int deviceIndex)
    {
        if (!IsInitialized)
            throw new OwnaudioException("Audio engine is not initialized.");

        if (deviceIndex < 0 || deviceIndex >= _outputDevices.Count)
            throw new OwnaudioException($"Device index {deviceIndex} is out of range (0-{_outputDevices.Count - 1}).");

        return _outputDevices[deviceIndex];
    }

    /// <summary>
    /// Gets information about a specific device by its index in the input device list.
    /// </summary>
    /// <param name="deviceIndex">The zero-based index of the device.</param>
    /// <returns>AudioDeviceInfo for the specified device.</returns>
    /// <exception cref="OwnaudioException">Exception if audio engine is not initialized or index is out of range.</exception>
    public static AudioDeviceInfo GetInputDeviceInfo(int deviceIndex)
    {
        if (!IsInitialized)
            throw new OwnaudioException("Audio engine is not initialized.");

        if (deviceIndex < 0 || deviceIndex >= _inputDevices.Count)
            throw new OwnaudioException($"Device index {deviceIndex} is out of range (0-{_inputDevices.Count - 1}).");

        return _inputDevices[deviceIndex];
    }
}
