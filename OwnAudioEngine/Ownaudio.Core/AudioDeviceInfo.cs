using System;

namespace Ownaudio.Core
{
    /// <summary>
    /// One audio device as the platform sees it.
    /// </summary>
    public sealed class AudioDeviceInfo
    {
        /// <summary>
        /// Platform-side unique id.
        /// </summary>
        public string DeviceId { get; }

        /// <summary>
        /// Name a human would recognise.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Who owns the device: "Wasapi", "CoreAudio", "PulseAudio", "Portaudio.Asio", "Miniaudio"...
        /// </summary>
        public string EngineName { get; }

        /// <summary>
        /// Capture side.
        /// </summary>
        public bool IsInput { get; }

        /// <summary>
        /// Render side.
        /// </summary>
        public bool IsOutput { get; }

        /// <summary>
        /// System default for its type.
        /// </summary>
        public bool IsDefault { get; }

        /// <summary>
        /// Enabled, disabled, unplugged, whatever.
        /// </summary>
        public AudioDeviceState State { get; }

        /// <summary>
        /// Input channels the hw can do. 0 = no input, or we simply don't know.
        /// </summary>
        public int MaxInputChannels { get; }

        /// <summary>
        /// Output channels the hw can do. 0 = no output or unknown.
        /// </summary>
        public int MaxOutputChannels { get; }

        /// <summary>
        /// Short version, channel counts default to 0.
        /// </summary>
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
        /// Full version, with the channel counts filled in. engineName is who owns
        /// the device (Wasapi, CoreAudio, Miniaudio...).
        /// </summary>
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
        /// Name, engine and direction — for logs and device pickers.
        /// </summary>
        public override string ToString()
        {
            string type = IsInput && IsOutput ? "Duplex" : IsInput ? "Input" : "Output";
            return $"{Name} [{EngineName}] ({type}){(IsDefault ? " [Default]" : "")}";
        }
    }

    /// <summary>
    /// Device state flags, values match the Windows ones.
    /// </summary>
    public enum AudioDeviceState
    {
        /// <summary>
        /// Alive and usable.
        /// </summary>
        Active = 0x00000001,

        /// <summary>
        /// Turned off.
        /// </summary>
        Disabled = 0x00000002,

        /// <summary>
        /// Gone from the system.
        /// </summary>
        NotPresent = 0x00000004,

        /// <summary>
        /// Jack pulled.
        /// </summary>
        Unplugged = 0x00000008,

        /// <summary>
        /// Everything above.
        /// </summary>
        All = 0x0000000F
    }
}
