namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Identity token for an external VST3 plugin hosted in a native effect chain. The
/// plugin has no managed parameter surface of its own — OwnAudioVst drives it, Rust
/// only pushes audio through. AddVst hands this back so we can find the effect again.
/// </summary>
public sealed class NativeVstEffect
{
}
