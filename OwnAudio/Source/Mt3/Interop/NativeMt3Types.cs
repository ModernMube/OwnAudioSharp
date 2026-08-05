using System.Runtime.InteropServices;

namespace OwnaudioNET.Features.Extensions.Mt3Interop;

/// <summary>
/// Mirror of the Rust NativeMt3Note. Every field is 4 bytes so the layout matches without
/// padding on either side.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMt3Note
{
    /// <summary>Onset in seconds.</summary>
    public float StartTime;

    /// <summary>Offset in seconds.</summary>
    public float EndTime;

    /// <summary>MIDI pitch.</summary>
    public int Pitch;

    /// <summary>MIDI velocity, 1..127.</summary>
    public int Velocity;

    /// <summary>MIDI program; 0 for drums.</summary>
    public int Program;

    /// <summary>Non-zero when it's percussion.</summary>
    public int IsDrum;
}

/// <summary>
/// Mirror of the Rust NativeMt3Options.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMt3Options
{
    /// <summary>ONNX Runtime intra-op threads; 0 lets the runtime pick.</summary>
    public int Threads;

    /// <summary>Non-zero to drop drums before they cross the boundary.</summary>
    public int SkipDrums;
}

/// <summary>
/// What the native side returns. Mirrors Mt3ErrorCode in error_code.rs.
/// </summary>
internal enum Mt3ErrorCode
{
    /// <summary>All good.</summary>
    Success = 0,

    /// <summary>A model or vocab file wasn't at the given path.</summary>
    ModelNotFound = 1,

    /// <summary>ONNX Runtime refused the graph.</summary>
    ModelLoadFailed = 2,

    /// <summary>vocab.json is malformed.</summary>
    VocabInvalid = 3,

    /// <summary>A session ran but gave back something unusable.</summary>
    InferenceFailed = 4,

    /// <summary>Resampling blew up.</summary>
    ResampleFailed = 5,

    /// <summary>A required pointer was null.</summary>
    NullPointer = 6,

    /// <summary>Handle doesn't point to a live transcriber.</summary>
    InvalidHandle = 7,

    /// <summary>A path wasn't valid UTF-8.</summary>
    InvalidUtf8 = 8,

    /// <summary>A panic was caught at the boundary.</summary>
    InternalPanic = 9,

    /// <summary>I/O error.</summary>
    IoError = 10
}
