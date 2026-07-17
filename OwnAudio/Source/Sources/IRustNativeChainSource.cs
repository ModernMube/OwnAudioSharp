using Ownaudio.Audio.Tracks;

namespace OwnaudioNET.Sources;

/// <summary>
/// Sources backed by a native AudioTrack in the rust-native chain (File/Sample/InputSource).
/// Lets the mixer facade drive routing, metering and per-track fx through one handle - the audio
/// itself is produced fully on the native side, managed is just a controller.
/// </summary>
internal interface IRustNativeChainSource
{
    /// <summary>
    /// The native track rendering this source, null on legacy or before the backend exists.
    /// </summary>
    AudioTrack? RustTrack { get; }
}
