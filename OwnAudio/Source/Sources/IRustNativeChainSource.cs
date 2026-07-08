using Ownaudio.Audio.Tracks;

namespace OwnaudioNET.Sources;

/// <summary>
/// Implemented by audio sources that are backed by a native <see cref="AudioTrack"/> in the
/// Rust-native chain (currently <see cref="FileSource"/> and <see cref="SampleSource"/>).
/// </summary>
/// <remarks>
/// Lets the <c>AudioMixer</c> Rust-native facade drive the generic, source-type-agnostic parts of
/// the chain — output-channel routing, metering, and per-track effect routing — through a single
/// handle, without a managed audio path. The audio itself is produced entirely on the native side
/// (a native file / memory / capture source feeding the track), so the managed side is only a
/// controller and the GC never touches the render path.
/// </remarks>
internal interface IRustNativeChainSource
{
    /// <summary>
    /// Gets the native track rendering this source in the Rust-native chain, or
    /// <see langword="null"/> when running legacy or before the backend is created.
    /// </summary>
    AudioTrack? RustTrack { get; }
}
