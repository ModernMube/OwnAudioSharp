namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Opaque identity token for an external VST3 plugin hosted natively in a Rust effect chain.
/// </summary>
/// <remarks>
/// Unlike the built-in effect wrappers, a native VST bridge has no managed parameter surface of its
/// own — the plugin is created and parameter-controlled through the <c>OwnAudioVst</c> host, and the
/// Rust side only forwards audio to it. This token is returned by <c>AddVst</c> so the effect can be
/// located again for removal, without pretending to expose a DSP wrapper.
/// </remarks>
public sealed class NativeVstEffect
{
}
