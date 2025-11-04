using System;

namespace Ownaudio.Core
{
    /// <summary>
    /// Configuration parameters for audio engine initialization.
    /// </summary>
    public sealed class AudioConfig
    {
        /// <summary>
        /// Sample rate in Hz (e.g., 44100, 48000).
        /// Default: 48000 Hz.
        /// </summary>
        public int SampleRate { get; set; } = 48000;

        /// <summary>
        /// Number of audio channels (1 = mono, 2 = stereo).
        /// Default: 2 (stereo).
        /// </summary>
        public int Channels { get; set; } = 2;

        /// <summary>
        /// Desired buffer size in frames.
        /// Actual buffer size may differ - check FramesPerBuffer after initialization.
        /// Smaller = lower latency, higher CPU usage.
        /// Default: 512 frames (~10.6ms at 48kHz).
        /// </summary>
        public int BufferSize { get; set; } = 512;

        /// <summary>
        /// Enable input (recording) functionality.
        /// Default: false (output only).
        /// </summary>
        public bool EnableInput { get; set; } = false;

        /// <summary>
        /// Enable output (playback) functionality.
        /// Default: true.
        /// </summary>
        public bool EnableOutput { get; set; } = true;

        /// <summary>
        /// Output device ID (platform-specific, null = default output device).
        /// Use <see cref="IDeviceEnumerator"/> to get available device IDs.
        /// Default: null (use default audio output device).
        /// </summary>
        public string? OutputDeviceId { get; set; } = null;

        /// <summary>
        /// Input device ID (platform-specific, null = default input device).
        /// Use <see cref="IDeviceEnumerator"/> to get available device IDs.
        /// Default: null (use default audio input device).
        /// </summary>
        public string? InputDeviceId { get; set; } = null;

        /// <summary>
        /// Validates the configuration parameters.
        /// </summary>
        /// <returns>True if configuration is valid, false otherwise.</returns>
        public bool Validate()
        {
            if (SampleRate <= 0 || SampleRate > 192000)
                return false;

            if (Channels <= 0 || Channels > 32)
                return false;

            if (BufferSize <= 0 || BufferSize > 16384)
                return false;

            if (!EnableInput && !EnableOutput)
                return false;

            return true;
        }

        /// <summary>
        /// Creates a default audio configuration (48kHz, stereo, 512 frames).
        /// </summary>
        public static AudioConfig Default => new AudioConfig();

        /// <summary>
        /// Creates a low-latency configuration (48kHz, stereo, 128 frames).
        /// </summary>
        public static AudioConfig LowLatency => new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 128
        };

        /// <summary>
        /// Creates a high-latency configuration (48kHz, stereo, 2048 frames).
        /// Useful for reducing CPU usage when latency is not critical.
        /// </summary>
        public static AudioConfig HighLatency => new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 2048
        };
    }
}