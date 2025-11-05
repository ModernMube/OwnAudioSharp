using OwnaudioNET;

namespace OwnaudioLegacy.Engines;

/// <summary>
/// Represents configuration class that can be passed to audio engine.
/// This class cannot be inherited.
/// </summary>
public sealed class AudioEngineOutputOptions
{
    /// <summary>
    /// Initializes <see cref="AudioEngineOutputOptions"/>.
    /// </summary>
    /// <param name="device">Desired output device, see: <see cref="OwnAudio.OutputDevices"/>.</param>
    /// <param name="channels">Desired audio channels, or fallback to maximum channels.</param>
    /// <param name="sampleRate">Desired output sample rate.</param>
    /// <param name="latency">Desired output latency.</param>
    public AudioEngineOutputOptions(AudioDevice device, OwnAudioEngine.EngineChannels channels, int sampleRate, double latency)
    {
        Device = device;
        Channels = FallbackChannelOutCount(Device, channels);
        SampleRate = sampleRate;
        Latency = latency;
    }

    /// <summary>
    /// Initializes <see cref="AudioEngineOutputOptions"/> by using default output device.
    /// </summary>
    /// <param name="channels">Desired audio channels, or fallback to maximum channels.</param>
    /// <param name="sampleRate">Desired output sample rate.</param>
    /// <param name="latency">Desired output latency.</param>
    public AudioEngineOutputOptions(OwnAudioEngine.EngineChannels channels, int sampleRate, double latency)
    {
        Device = new AudioDevice();
        Channels = FallbackChannelOutCount(Device, channels);
        SampleRate = sampleRate;
        Latency = latency;
    }

    /// <summary>
    /// Initializes <see cref="AudioEngineOutputOptions"/> by using default output device
    /// and its default high output latency.
    /// </summary>
    /// <param name="channels">Desired audio channels, or fallback to maximum channels.</param>
    /// <param name="sampleRate">Desired output sample rate.</param>
    public AudioEngineOutputOptions(OwnAudioEngine.EngineChannels channels, int sampleRate)
    {
        Device = new AudioDevice();
        Channels = FallbackChannelOutCount(Device, channels);
        SampleRate = sampleRate;
        Latency = Device.DefaultHighOutputLatency;
    }

    /// <summary>
    /// Initializes <see cref="AudioEngineOutputOptions"/> by using default output device.
    /// Sample rate will be set to 44100, channels to 2 (or max) and latency to default high. 
    /// </summary>
    public AudioEngineOutputOptions()
    {
        Device = new AudioDevice();
        Channels = FallbackChannelOutCount(Device, OwnAudioEngine.EngineChannels.Stereo);
        SampleRate = OwnaudioNet.Engine!.Config.SampleRate;
        Latency = Device.DefaultHighOutputLatency;
    }

    /// <summary>
    /// Gets desired output device.
    /// See: <see cref="OwnAudio.OutputDevices"/> and <see cref="OwnAudio.DefaultOutputDevice"/>.
    /// </summary>
    public AudioDevice Device { get; }

    /// <summary>
    /// Gets desired number of audio channels. This might fallback to maximum device output channels,
    /// see: <see cref="AudioDevice.MaxOutputChannels"/>.
    /// </summary>
    public OwnAudioEngine.EngineChannels Channels { get; }

    /// <summary>
    /// Gets desired audio sample rate.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// Gets desired output latency.
    /// </summary>
    public double Latency { get; }

    private static OwnAudioEngine.EngineChannels FallbackChannelOutCount(AudioDevice device, OwnAudioEngine.EngineChannels desiredChannel)
    {
        return (int)desiredChannel > device.MaxOutputChannels ? OwnAudioEngine.EngineChannels.Stereo : desiredChannel;
    }
}
