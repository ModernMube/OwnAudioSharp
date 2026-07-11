namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Opaque identity token for the SmartMaster composite mastering chain hosted natively in a Rust
/// effect chain.
/// </summary>
/// <remarks>
/// Like <see cref="NativeVstEffect"/>, the SmartMaster native effect has no strongly-typed managed
/// wrapper of its own: the managed <c>SmartMasterEffect</c> remains the parameter model and preset
/// owner, and its configuration is mirrored onto the native effect by numeric parameter id. This
/// token is returned by <c>Add(EffectType.SmartMaster, …)</c> so the effect can be located again for
/// parameter updates and removal.
/// </remarks>
public sealed class NativeSmartMasterEffect
{
}
