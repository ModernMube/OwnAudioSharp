using Ownaudio.Core;
using Ownaudio.Decoders;
using OwnaudioNET;

namespace OwnaudioLegacy.Engines;

/// <summary>
/// Represents configuration class that can be passed to audio engine.
/// This class cannot be inherited.
/// </summary>
public sealed class AudioEngineInputOptions
{
    /// <summary>
    /// Initializes <see cref="AudioEngineInputOptions"/>.
    /// </summary>
    /// <param name="device">Desired input device, see: <see cref="OwnAudio.InputDevices"/>.</param>
    /// <param name="channels">Desired audio channels, or fallback to maximum channels.</param>
    /// <param name="sampleRate">Desired input sample rate.</param>
    /// <param name="latency">Desired input latency.</param>
    public AudioEngineInputOptions(AudioDevice device, OwnAudioEngine.EngineChannels channels, int sampleRate, double latency)
    {
        Device = device;
        Channels = FallbackChannelCount(Device, channels);
        SampleRate = sampleRate;
        Latency = latency;
    }

    /// <summary>
    /// Initializes <see cref="AudioEngineInputOptions"/> by using default input device.
    /// </summary>
    /// <param name="channels">Desired audio channels, or fallback to maximum channels.</param>
    /// <param name="sampleRate">Desired input sample rate.</param>
    /// <param name="latency">Desired input latency.</param>
    public AudioEngineInputOptions(OwnAudioEngine.EngineChannels channels, int sampleRate, double latency)
    {
        Device = new AudioDevice();
        Channels = FallbackChannelCount(Device, channels);
        SampleRate = sampleRate;
        Latency = latency;
    }

    /// <summary>
    /// Initializes <see cref="AudioEngineInputOptions"/> by using default input device
    /// and its default high input latency.
    /// </summary>
    /// <param name="channels">Desired audio channels, or fallback to maximum channels.</param>
    /// <param name="sampleRate">Desired input sample rate.</param>
    public AudioEngineInputOptions(OwnAudioEngine.EngineChannels channels, int sampleRate)
    {
        Device = new AudioDevice();
        Channels = FallbackChannelCount(Device, channels);
        SampleRate = sampleRate;
        Latency = Device.DefaultLowInputLatency;
    }

    /// <summary>
    /// Initializes <see cref="AudioEngineInputOptions"/> by using default input device.
    /// Sample rate will be set to 44100, channels to 2 (or max) and latency to default high. 
    /// </summary>
    public AudioEngineInputOptions()
    {
        Device = new AudioDevice();
        Channels = FallbackChannelCount(Device, OwnAudioEngine.EngineChannels.Mono);
        SampleRate = OwnaudioNet.Engine!.Config.SampleRate;
        Latency = Device.DefaultLowInputLatency;
    }

    /// <summary>
    /// Gets desired input device.
    /// See: <see cref="OwnAudio.InputDevices"/> and <see cref="OwnAudio.DefaultInputDevice"/>.
    /// </summary>
    public AudioDevice Device { get; }

    /// <summary>
    /// Gets desired number of audio channels. This might fallback to maximum device input channels,
    /// see: <see cref="AudioDevice.MaxInputChannels"/>.
    /// </summary>
    public OwnAudioEngine.EngineChannels Channels { get; }

    /// <summary>
    /// Gets desired audio sample rate.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// Gets desired input latency.
    /// </summary>
    public double Latency { get; }

    private static OwnAudioEngine.EngineChannels FallbackChannelCount(AudioDevice device, OwnAudioEngine.EngineChannels desiredChannel)
    {
        return (int)desiredChannel > device.MaxInputChannels ? OwnAudioEngine.EngineChannels.Stereo : desiredChannel;
    }
}
