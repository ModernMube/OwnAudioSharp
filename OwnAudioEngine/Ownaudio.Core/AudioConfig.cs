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
        /// Playback width, when it differs from Channels. null (the default) means "use Channels",
        /// which is what everyone got before this existed. A 2 in / 8 out interface needs the two
        /// directions described separately, and Channels alone can't do that.
        /// </summary>
        public int? OutputChannels { get; set; } = null;

        /// <summary>
        /// Capture width, same deal. null = Channels.
        /// </summary>
        public int? InputChannels { get; set; } = null;

        /// <summary>
        /// Wanted buffer size in frames. The driver may round it — compare FramesPerBuffer
        /// against OutputCallbackFrames once audio runs. Smaller = less latency, more CPU.
        /// </summary>
        public int BufferSize { get; set; } = 512;

        /// <summary>
        /// How much audio the engine keeps queued ahead of the DAC, in ms. This is the bulk of
        /// the output latency — the producer keeps the ring topped up, so it's paid on every
        /// buffer regardless of BufferSize. 100 is the safe default; live monitoring wants
        /// 10-20. Anything under three device buffers gets pulled back up.
        /// </summary>
        /// <remarks>
        /// Sizes the render ring on the push path, where managed code hands the engine samples
        /// and a native ring feeds the device: <c>AudioEngineWrapper.Send</c> and any buffered
        /// output stream. An <c>AudioMixer</c> on the native session has no ring to size — it
        /// renders inside the device callback, which is the lower latency shape — so there
        /// <see cref="BufferSize"/> is the whole knob. Either way the engine reports which one
        /// it opened with, and out of range (1..<see cref="MaxOutputRingMilliseconds"/>) is a
        /// logged warning rather than a silent fallback.
        /// </remarks>
        public int OutputRingMilliseconds { get; set; } = 100;

        /// <summary>
        /// Deepest render ring anyone may ask for, in ms.
        /// </summary>
        public const int MaxOutputRingMilliseconds = 2000;

        /// <summary>
        /// <see cref="OutputRingMilliseconds"/> in frames at <see cref="SampleRate"/>, inside the
        /// range the native side accepts. 0 when the request is out of range, which every engine
        /// reads as "use your own default". The engine still pulls anything shallower than three
        /// device buffers back up, so read the granted depth off the stream rather than assuming
        /// this is what you got.
        /// </summary>
        public int OutputRingFrames
        {
            get
            {
                if (OutputRingMilliseconds <= 0 || OutputRingMilliseconds > MaxOutputRingMilliseconds)
                    return 0;

                long _frames = (long)SampleRate * OutputRingMilliseconds / 1000;
                return (int)Math.Clamp(_frames, MinRingFrames, MaxRingFrames);
            }
        }

        /// <summary>
        /// Shallowest render ring the native side accepts, in frames.
        /// </summary>
        public const int MinRingFrames = 16;

        /// <summary>
        /// Deepest render ring the native side accepts, in frames.
        /// </summary>
        public const int MaxRingFrames = 384_000;

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
        public bool Validate() => Validate(out _);

        /// <summary>
        /// Same check, but it says which field was wrong. A config is rejected before a single
        /// device is touched, so "invalid configuration" on its own leaves the host guessing
        /// between nine settings — this is what the engine reports and logs instead.
        /// </summary>
        /// <param name="error">null when valid, otherwise the first field that failed and why</param>
        /// <returns>True if configuration is valid, false otherwise.</returns>
        public bool Validate(out string? error)
        {
            error = null;

            if (SampleRate <= 0 || SampleRate > 192000)
                error = $"SampleRate {SampleRate} is outside 1..192000";
            else if (Channels <= 0 || Channels > 256)
                error = $"Channels {Channels} is outside 1..256";
            else if (OutputChannels is int _out && (_out <= 0 || _out > 256))
                error = $"OutputChannels {_out} is outside 1..256";
            else if (InputChannels is int _in && (_in <= 0 || _in > 256))
                error = $"InputChannels {_in} is outside 1..256";
            else if (BufferSize <= 0 || BufferSize > 16384)
                error = $"BufferSize {BufferSize} is outside 1..16384";
            else if (OutputRingMilliseconds <= 0 || OutputRingMilliseconds > MaxOutputRingMilliseconds)
                error = $"OutputRingMilliseconds {OutputRingMilliseconds} is outside 1..{MaxOutputRingMilliseconds}";
            else if (!EnableInput && !EnableOutput)
                error = "both EnableInput and EnableOutput are off, there would be nothing to run";
            else if (!_selectorsOk(InputChannelSelectors, EffectiveInputChannels))
                error = $"InputChannelSelectors must hold {EffectiveInputChannels} distinct channels "
                    + $"below {MaxRouteChannels}";
            else if (!_selectorsOk(OutputChannelSelectors, EffectiveOutputChannels))
                error = $"OutputChannelSelectors must hold {EffectiveOutputChannels} distinct channels "
                    + $"below {MaxRouteChannels}";

            return error is null;
        }

        /// <summary>
        /// The playback width actually asked of the device.
        /// </summary>
        public int EffectiveOutputChannels => OutputChannels ?? Channels;

        /// <summary>
        /// The capture width actually asked of the device.
        /// </summary>
        public int EffectiveInputChannels => InputChannels ?? Channels;

        /// <summary>
        /// Channel map has to be as long as that direction's width, in range, and no channel twice.
        /// The 16 cap is the engine's per-track route limit, nothing routes past it.
        /// </summary>
        private static bool _selectorsOk(int[]? map, int channels)
        {
            if (map == null || map.Length == 0) return true;
            if (map.Length != channels) return false;

            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] < 0 || map[i] >= MaxRouteChannels) return false;
                for (int j = i + 1; j < map.Length; j++)
                    if (map[i] == map[j]) return false;
            }

            return true;
        }

        /// <summary>
        /// How many channels a per-track route can reach, matching the engine's MAX_ROUTE_CHANNELS.
        /// </summary>
        public const int MaxRouteChannels = 16;

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
