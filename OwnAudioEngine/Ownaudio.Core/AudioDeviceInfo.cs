using System;

namespace Ownaudio.Core
{
    /// <summary>
    /// Represents information about an audio device.
    /// </summary>
    public sealed class AudioDeviceInfo
    {
        /// <summary>
        /// Gets the unique device identifier used by the platform API.
        /// </summary>
        public string DeviceId { get; }

        /// <summary>
        /// Gets the human-readable device name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the name of the audio engine that manages this device.
        /// Examples: "Wasapi", "CoreAudio", "PulseAudio", "Portaudio.Asio", "Portaudio.Wasapi", "Miniaudio"
        /// </summary>
        public string EngineName { get; }

        /// <summary>
        /// Gets a value indicating whether this is an input (capture) device.
        /// </summary>
        public bool IsInput { get; }

        /// <summary>
        /// Gets a value indicating whether this is an output (render) device.
        /// </summary>
        public bool IsOutput { get; }

        /// <summary>
        /// Gets a value indicating whether this is the default device for its type.
        /// </summary>
        public bool IsDefault { get; }

        /// <summary>
        /// Gets the device state (enabled, disabled, unplugged, etc.).
        /// </summary>
        public AudioDeviceState State { get; }

        /// <summary>
        /// Gets the maximum number of input channels supported by this device.
        /// Returns 0 if the device does not support input or if the information is unavailable.
        /// </summary>
        public int MaxInputChannels { get; }

        /// <summary>
        /// Gets the maximum number of output channels supported by this device.
        /// Returns 0 if the device does not support output or if the information is unavailable.
        /// </summary>
        public int MaxOutputChannels { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioDeviceInfo"/> class.
        /// </summary>
        /// <param name="deviceId">The unique device identifier.</param>
        /// <param name="name">The human-readable device name.</param>
        /// <param name="engineName">The name of the audio engine managing this device.</param>
        /// <param name="isInput">Indicates whether this is an input device.</param>
        /// <param name="isOutput">Indicates whether this is an output device.</param>
        /// <param name="isDefault">Indicates whether this is the default device.</param>
        /// <param name="state">The device state.</param>
        public AudioDeviceInfo(
            string deviceId,
            string name,
            string engineName,
            bool isInput,
            bool isOutput,
            bool isDefault,
            AudioDeviceState state)
            : this(deviceId, name, engineName, isInput, isOutput, isDefault, state, 0, 0)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioDeviceInfo"/> class with channel count information.
        /// </summary>
        /// <param name="deviceId">The unique device identifier.</param>
        /// <param name="name">The human-readable device name.</param>
        /// <param name="engineName">The name of the audio engine managing this device.</param>
        /// <param name="isInput">Indicates whether this is an input device.</param>
        /// <param name="isOutput">Indicates whether this is an output device.</param>
        /// <param name="isDefault">Indicates whether this is the default device.</param>
        /// <param name="state">The device state.</param>
        /// <param name="maxInputChannels">Maximum number of input channels supported.</param>
        /// <param name="maxOutputChannels">Maximum number of output channels supported.</param>
        public AudioDeviceInfo(
            string deviceId,
            string name,
            string engineName,
            bool isInput,
            bool isOutput,
            bool isDefault,
            AudioDeviceState state,
            int maxInputChannels,
            int maxOutputChannels)
        {
            DeviceId = deviceId;
            Name = name;
            EngineName = engineName ?? "Unknown";
            IsInput = isInput;
            IsOutput = isOutput;
            IsDefault = isDefault;
            State = state;
            MaxInputChannels = maxInputChannels;
            MaxOutputChannels = maxOutputChannels;
        }

        /// <summary>
        /// Returns a string representation of this audio device.
        /// </summary>
        /// <returns>A string containing the device name, engine, and type.</returns>
        public override string ToString()
        {
            string type = IsInput && IsOutput ? "Duplex" : IsInput ? "Input" : "Output";
            string defaultMarker = IsDefault ? " [Default]" : "";
            return $"{Name} [{EngineName}] ({type}){defaultMarker}";
        }
    }

    /// <summary>
    /// Represents the state of an audio device.
    /// </summary>
    public enum AudioDeviceState
    {
        /// <summary>
        /// The device is active and available.
        /// </summary>
        Active = 0x00000001,

        /// <summary>
        /// The device is disabled.
        /// </summary>
        Disabled = 0x00000002,

        /// <summary>
        /// The device is not present (unplugged).
        /// </summary>
        NotPresent = 0x00000004,

        /// <summary>
        /// The device is unplugged.
        /// </summary>
        Unplugged = 0x00000008,

        /// <summary>
        /// All device states.
        /// </summary>
        All = 0x0000000F
    }
}
