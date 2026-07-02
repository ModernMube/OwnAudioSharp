using System.Runtime.CompilerServices;
using Ownaudio.Core;

namespace OwnaudioNET.Engine;

/// <summary>
/// Registers the Rust-backed <see cref="RustAudioEngine"/> as the creator for
/// <see cref="Ownaudio.Core.AudioEngineFactory"/> at module load time, so callers of the
/// low-level factory receive the cpal-driven engine without any setup code. This mirrors the
/// registration the deleted PortAudio/MiniAudio layer performed, now pointing at the Rust engine.
/// </summary>
internal static class RustAudioEngineRegistrar
{
    /// <summary>
    /// Runs automatically when the assembly is loaded and wires the Rust engine creator into the
    /// core factory.
    /// </summary>
    [ModuleInitializer]
    internal static void Register()
    {
        Ownaudio.Core.AudioEngineFactory.Register(() => new RustAudioEngine());
    }
}
