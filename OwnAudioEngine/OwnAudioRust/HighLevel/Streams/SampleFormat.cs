namespace Ownaudio.Audio.Streams;

/// <summary>
/// Sample data format used in high-level audio streams.
/// </summary>
/// <remarks>
/// Numeric values intentionally match <c>Ownaudio.Safe.SampleFormat</c> so that
/// a direct cast is safe without a look-up table.
/// </remarks>
public enum SampleFormat
{
    /// <summary>32-bit IEEE 754 float — recommended for all DSP work.</summary>
    Float32 = 0,

    /// <summary>Signed 16-bit integer.</summary>
    Int16 = 1,

    /// <summary>Unsigned 16-bit integer.</summary>
    UInt16 = 2,
}
