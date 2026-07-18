namespace Ownaudio.Decoders;

/// <summary>
/// What a decoder read call gives back.
/// </summary>
public readonly struct AudioDecoderResult
{
    /// <summary>
    /// Old allocating path — carries a whole frame object. eof means the decoder
    /// hit the end of the stream.
    /// </summary>
    public AudioDecoderResult(AudioFrame? frame, bool succeeded, bool eof, string? errorMessage = default)
    {
        Frame = frame;
        IsSucceeded = succeeded;
        IsEOF = eof;
        ErrorMessage = errorMessage;
        FramesRead = 0;
        PresentationTime = frame?.PresentationTime ?? 0.0;
    }

    /// <summary>
    /// Zero-alloc path: framesRead is how much landed in the caller's buffer.
    /// </summary>
    public AudioDecoderResult(int framesRead, bool succeeded, bool eof, string? errorMessage = default)
    {
        FramesRead = framesRead;
        IsSucceeded = succeeded;
        IsEOF = eof;
        ErrorMessage = errorMessage;
        Frame = null;
        PresentationTime = 0.0;
    }

    /// <summary>
    /// Same as above but with a pts in ms.
    /// </summary>
    public AudioDecoderResult(int framesRead, double pts, bool succeeded, bool eof, string? errorMessage = default)
    {
        FramesRead = framesRead;
        PresentationTime = pts;
        IsSucceeded = succeeded;
        IsEOF = eof;
        ErrorMessage = errorMessage;
        Frame = null;
    }

    /// <summary>
    /// The decoded frame, null on the zero-alloc path.
    /// </summary>
    public AudioFrame? Frame { get; }

    /// <summary>
    /// Frames written into the output buffer.
    /// </summary>
    public int FramesRead { get; }

    /// <summary>
    /// Presentation timestamp in ms.
    /// </summary>
    public double PresentationTime { get; }

    /// <summary>
    /// Read went fine.
    /// </summary>
    public bool IsSucceeded { get; }

    /// <summary>
    /// End of stream, nothing more to pull.
    /// </summary>
    public bool IsEOF { get; }

    /// <summary>
    /// Why it failed, if it did.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Good read, pts in milliseconds.
    /// </summary>
    public static AudioDecoderResult CreateSuccess(int framesRead, double pts = 0)
        => new AudioDecoderResult(framesRead, pts, succeeded: true, eof: false);

    /// <summary>
    /// We're done, nothing left.
    /// </summary>
    public static AudioDecoderResult CreateEOF()
        => new AudioDecoderResult(0, succeeded: false, eof: true);

    /// <summary>
    /// Something blew up mid-read.
    /// </summary>
    public static AudioDecoderResult CreateError(string errorMessage)
        => new AudioDecoderResult(0, succeeded: false, eof: false, errorMessage);
}
