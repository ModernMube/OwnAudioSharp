using System;

namespace Ownaudio.Core
{
    /// <summary>
    /// What the engine gets handed at init time.
    /// </summary>
    public sealed class AudioConfig
    {
        /// <summary>
        /// Sample rate in Hz.
        /// </summary>
        public int SampleRate { get; set; } = 48000;

        /// <summary>
        /// 1 = mono, 2 = stereo.
        /// </summary>
        public int Channels { get; set; } = 2;

        /// <summary>
        /// Wanted buffer size in frames. The device may hand back something else,
        /// check FramesPerBuffer after init. Smaller = less latency, more CPU.
        /// </summary>
        public int BufferSize { get; set; } = 512;

        /// <summary>
        /// Recording on/off.
        /// </summary>
        public bool EnableInput { get; set; } = false;

        /// <summary>
        /// Playback on/off.
        /// </summary>
        public bool EnableOutput { get; set; } = true;

        /// <summary>
        /// Output device id, null = system default. Ids come from <see cref="IDeviceEnumerator"/>.
        /// </summary>
        public string? OutputDeviceId { get; set; } = null;

        /// <summary>
        /// Input device id, null = system default.
        /// </summary>
        public string? InputDeviceId { get; set; } = null;

        /// <summary>
        /// Host API pick. PortAudio only, MiniAudio couldn't care less.
        /// </summary>
        public EngineHostType HostType { get; set; } = EngineHostType.None;

        /// <summary>
        /// Which physical input channels we actually want. ASIO does it in hw, everything
        /// else routes in the callback. null/empty = first N channels. Length must equal Channels.
        /// </summary>
        public int[]? InputChannelSelectors { get; set; } = null;

        /// <summary>
        /// Same deal for output. [4, 5] sends logical 0/1 to physical 4/5.
        /// </summary>
        public int[]? OutputChannelSelectors { get; set; } = null;

        /// <summary>
        /// Device vanished mid-stream? true = hop to the system default and keep going,
        /// then hop back when it returns. false = sit in DeviceDisconnected and wait.
        /// </summary>
        public bool FallbackToDefaultOnDisconnect { get; set; } = true;

        /// <summary>
        /// Sanity check before we hand this to the engine.
        /// </summary>
        /// <returns>True if configuration is valid, false otherwise.</returns>
        public bool Validate()
        {
            if (SampleRate <= 0 || SampleRate > 192000) return false;
            if (Channels <= 0 || Channels > 256) return false;
            if (BufferSize <= 0 || BufferSize > 16384) return false;
            if (!EnableInput && !EnableOutput) return false;

            return _selectorsOk(InputChannelSelectors) && _selectorsOk(OutputChannelSelectors);
        }

        /// <summary>
        /// Channel map has to be as long as Channels, in range, and no channel twice.
        /// </summary>
        private bool _selectorsOk(int[]? map)
        {
            if (map == null || map.Length == 0) return true;
            if (map.Length != Channels) return false;

            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] < 0 || map[i] > 256) return false;
                for (int j = i + 1; j < map.Length; j++)
                    if (map[i] == map[j]) return false;
            }

            return true;
        }

        /// <summary>
        /// 48kHz, stereo, 512 frames.
        /// </summary>
        public static AudioConfig Default => new AudioConfig();

        /// <summary>
        /// Same, but 128 frames for the low latency crowd.
        /// </summary>
        public static AudioConfig LowLatency => new AudioConfig { BufferSize = 128 };

        /// <summary>
        /// 2048 frames — fat buffers, cheap CPU, latency doesn't matter here.
        /// </summary>
        public static AudioConfig HighLatency => new AudioConfig { BufferSize = 2048 };
    }
}
