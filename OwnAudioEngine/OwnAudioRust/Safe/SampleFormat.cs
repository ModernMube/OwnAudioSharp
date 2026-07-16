namespace Ownaudio.Safe;

/// <summary>
/// Sample formats we hand over to the ffi side.
/// Keep the numbers as they are, OwnAudioSampleFormat in ownaudio_ffi.h relies on them.
/// </summary>
public enum SampleFormat
{
    /// 32 bit float, this is what the dsp chain wants.
    F32 = 0,

    /// Signed 16 bit pcm.
    I16 = 1,

    /// Unsigned 16 bit pcm.
    U16 = 2,

    /// Signed 32 bit, most asio drivers speak this natively.
    I32 = 3,
}
