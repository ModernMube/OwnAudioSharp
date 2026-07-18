namespace Ownaudio;

/// <summary>
/// A chunk of decoded audio plus the time it belongs to. Legacy allocating path only.
/// </summary>
public sealed class AudioFrame
{
    /// <summary>
    /// presentationTime is in milliseconds, data is Float32 ready for the device.
    /// </summary>
    public AudioFrame(double presentationTime, byte[] data)
    {
        PresentationTime = presentationTime;
        Data = data;
    }

    /// <summary>
    /// Where this frame sits on the timeline, in ms.
    /// </summary>
    public double PresentationTime { get; }

    /// <summary>
    /// Raw Float32 samples.
    /// </summary>
    public byte[] Data { get; }
}
