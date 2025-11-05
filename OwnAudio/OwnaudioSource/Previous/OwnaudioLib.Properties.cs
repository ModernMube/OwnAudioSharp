using System.Collections.Generic;

using OwnaudioLegacy.Exceptions;
using OwnaudioLegacy.Utilities;
using Ownaudio.Core;
using Ownaudio.Decoders;

namespace OwnaudioLegacy;

/// <summary>
/// Functions to retrieve, configure and manage the current Ownaudio environment, 
/// which affects the entire directory configuration.
/// </summary>
public static partial class OwnAudioEngine
{
    /// <summary>
    /// Define local variables
    /// </summary>
    private static AudioDeviceInfo? _defaultOutputDevice;
    private static AudioDeviceInfo? _defaultInputDevice;
    private static List<AudioDeviceInfo> _outputDevices = new List<AudioDeviceInfo>();
    private static List<AudioDeviceInfo> _inputDevices = new List<AudioDeviceInfo>();

    /// <summary>
    /// Boolean variable in which we store the value of whether FFmpeg is initialized or not.
    /// </summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    /// Audio engine used globally in the code
    /// </summary>
    public static IAudioEngine? Engine { get; private set; }

    /// <summary>
    /// Audio decoder used globally in the code
    /// </summary>
    public static IAudioDecoder? Decoder { get; set; }

    /// <summary>
    /// AudioDevice is the default output device used by the current system.
    /// </summary>
    /// <exception cref="OwnaudioException">Exception if PortAudio is not initialized.</exception>
    public static AudioDeviceInfo DefaultOutputDevice
    {
        get
        {
            Ensure.That<OwnaudioException>(IsInitialized, "Audio engine is not initialized.");
            return _defaultOutputDevice ?? throw new OwnaudioException("Default output device is not available.");
        }
    }

    /// <summary>
    /// AudioDevice is the default input device used by the current system.
    /// </summary>
    /// <exception cref="OwnaudioException">Exception if PortAudio is not initialized.</exception>
    public static AudioDeviceInfo DefaultInputDevice
    {
        get
        {
            Ensure.That<OwnaudioException>(IsInitialized, "Audio engine is not initialized.");
            return _defaultInputDevice ?? throw new OwnaudioException("Default input device is not available.");
        }
    }

    /// <summary>
    /// List of audio input devices available in the current system.
    /// <exception cref="OwnaudioException">Exception if PortAudio is not initialized.</exception>
    /// </summary>
    public static IReadOnlyCollection<AudioDeviceInfo> InputDevices
    {
        get
        {
            Ensure.That<OwnaudioException>(IsInitialized, "Audio engine is not initialized.");
            return _inputDevices;
        }
    }

    /// <summary>
    /// List of audio output devices available in the current system.
    /// <exception cref="OwnaudioException">Exception if PortAudio is not initialized.</exception>
    /// </summary>
    public static IReadOnlyCollection<AudioDeviceInfo> OutputDevices
    {
        get
        {
            Ensure.That<OwnaudioException>(IsInitialized, "Audio engine is not initialized.");
            return _outputDevices;
        }
    }

    /// <summary>
    /// Event raised when the default output device changes.
    /// Subscribe to this event to be notified when the system default output device changes.
    /// </summary>
    public static event System.EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged
    {
        add
        {
            if (Engine != null)
                Engine.OutputDeviceChanged += value;
        }
        remove
        {
            if (Engine != null)
                Engine.OutputDeviceChanged -= value;
        }
    }

    /// <summary>
    /// Event raised when the default input device changes.
    /// Subscribe to this event to be notified when the system default input device changes.
    /// </summary>
    public static event System.EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged
    {
        add
        {
            if (Engine != null)
                Engine.InputDeviceChanged += value;
        }
        remove
        {
            if (Engine != null)
                Engine.InputDeviceChanged -= value;
        }
    }

    /// <summary>
    /// Event raised when a device state changes (added, removed, enabled, disabled).
    /// Subscribe to this event to be notified about device state changes.
    /// </summary>
    public static event System.EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged
    {
        add
        {
            if (Engine != null)
                Engine.DeviceStateChanged += value;
        }
        remove
        {
            if (Engine != null)
                Engine.DeviceStateChanged -= value;
        }
    }
}
