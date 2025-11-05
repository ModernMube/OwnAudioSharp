namespace OwnaudioLegacy;

/// <summary>
/// A structure containing information about audio device capabilities.
/// </summary>
public readonly struct AudioDevice
{
    /// <summary>
    /// Initializes <see cref="AudioDevice"/> structure.
    /// </summary>
    /// <param name="deviceIndex">Audio device index.</param>
    /// <param name="name">Audio device name.</param>
    /// <param name="maxOutputChannels">Maximum allowed output channels.</param>
    /// <param name="maxInputChannels">Maximum allowed output channels.</param>
    /// <param name="defaultLowOutputLatency">Default low output latency.</param>
    /// <param name="defaultHighOutputLatency">Default high output latency.</param>
    /// <param name="defaultLowInputLatency">Default low output latency.</param>
    /// <param name="defaultHighInputLatency">Default high output latency.</param>
    /// <param name="defaultSampleRate">Default audio sample rate in the device.</param>
    public AudioDevice(
        int deviceIndex,
        string name,
        int maxOutputChannels,
        int maxInputChannels,
        double defaultLowOutputLatency,
        double defaultHighOutputLatency,
        double defaultLowInputLatency,
        double defaultHighInputLatency,
        int defaultSampleRate)
        {
        DeviceIndex = deviceIndex;
        Name = name;
        MaxOutputChannels = maxOutputChannels;
        MaxInputChannels = maxInputChannels;
        DefaultLowOutputLatency = defaultLowOutputLatency;
        DefaultHighOutputLatency = defaultHighOutputLatency;
        DefaultLowInputLatency = defaultLowInputLatency;
        DefaultHighInputLatency = defaultHighInputLatency;
        DefaultSampleRate = defaultSampleRate;
    }

    /// <summary>
    /// Gets audio device index.
    /// </summary>
    public int DeviceIndex { get; }

    /// <summary>
    /// Gets audio device name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets maximum allowed output audio channels.
    /// </summary>
    public int MaxOutputChannels { get; }

    /// <summary>
    /// Gets maximum allowed input audio channels.
    /// </summary>
    public int MaxInputChannels { get; }

    /// <summary>
    /// Gets default low output latency (for interactive performance).
    /// </summary>
    public double DefaultLowOutputLatency { get; }

    /// <summary>
    /// Gets default high output latency (recommended for playing audio files).
    /// </summary>
    public double DefaultHighOutputLatency { get; }

    /// <summary>
    /// Gets default low input latency (for interactive performance).
    /// </summary>
    public double DefaultLowInputLatency { get; }

    /// <summary>
    /// Gets default high input latency (recommended for recording audio files).
    /// </summary>
    public double DefaultHighInputLatency { get; }

    /// <summary>
    /// Gets default audio sample rate on this device.
    /// </summary>
    public int DefaultSampleRate { get; }
}
