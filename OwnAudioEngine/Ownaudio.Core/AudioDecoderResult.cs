namespace Ownaudio.Decoders;

/// <summary>
/// Represents result structure returned by audio decoder while reading audio frame.
/// </summary>
public readonly struct AudioDecoderResult
{
    /// <summary>
    /// Initializes <see cref="AudioDecoderResult"/> structure for the legacy, allocating path.
    /// </summary>
    /// <param name="frame">Decoded audio frame if successfully reads.</param>
    /// <param name="succeeded">Whether or not the frame is successfully reads.</param>
    /// <param name="eof">Whether or not the decoder reaches end-of-file.</param>
    /// <param name="errorMessage">An error message while reading audio frame.</param>
    public AudioDecoderResult(AudioFrame? frame, bool succeeded, bool eof, string? errorMessage = default)
    {
        Frame = frame;
        IsSucceeded = succeeded;
        IsEOF = eof;
        ErrorMessage = errorMessage;
        FramesRead = 0; // Not applicable in the old path
    }

    /// <summary>
    /// Initializes <see cref="AudioDecoderResult"/> structure for the new, zero-allocation path.
    /// </summary>
    /// <param name="framesRead">Number of frames read into the provided buffer.</param>
    /// <param name="succeeded">Whether or not the read was successful.</param>
    /// <param name="eof">Whether or not the decoder reached the end of the file.</param>
    /// <param name="errorMessage">An error message if the read failed.</param>
    public AudioDecoderResult(int framesRead, bool succeeded, bool eof, string? errorMessage = default)
    {
        FramesRead = framesRead;
        IsSucceeded = succeeded;
        IsEOF = eof;
        ErrorMessage = errorMessage;
        Frame = null;
    }

    /// <summary>
    /// Gets decoded audio frame if successfully reads.
    /// This will be <c>null</c> when using the zero-allocation read path.
    /// </summary>
    public AudioFrame? Frame { get; }

    /// <summary>
    /// Gets the number of frames read into the output buffer.
    /// This is used by the zero-allocation read path.
    /// </summary>
    public int FramesRead { get; }

    /// <summary>
    /// Gets whether or not the decoder is succesfully reading audio frame.
    /// </summary>
    public bool IsSucceeded { get; }

    /// <summary>
    /// Gets whether or not the decoder reaches end-of-file (cannot be continued) while reading audio frame. 
    /// </summary>
    public bool IsEOF { get; }

    /// <summary>
    /// Gets error message from the decoder while reading audio frame.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Creates a successful result with the number of frames read.
    /// </summary>
    /// <param name="framesRead">Number of frames read.</param>
    /// <param name="pts">Presentation timestamp in milliseconds.</param>
    /// <returns>A successful AudioDecoderResult.</returns>
    public static AudioDecoderResult CreateSuccess(int framesRead, double pts = 0)
    {
        return new AudioDecoderResult(framesRead, succeeded: true, eof: false);
    }

    /// <summary>
    /// Creates an EOF (end-of-file) result.
    /// </summary>
    /// <returns>An EOF AudioDecoderResult.</returns>
    public static AudioDecoderResult CreateEOF()
    {
        return new AudioDecoderResult(0, succeeded: false, eof: true);
    }

    /// <summary>
    /// Creates an error result with the specified error message.
    /// </summary>
    /// <param name="errorMessage">The error message.</param>
    /// <returns>An error AudioDecoderResult.</returns>
    public static AudioDecoderResult CreateError(string errorMessage)
    {
        return new AudioDecoderResult(0, succeeded: false, eof: false, errorMessage);
    }
}
