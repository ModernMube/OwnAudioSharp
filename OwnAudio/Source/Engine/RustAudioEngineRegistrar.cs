using System.Runtime.CompilerServices;
using Ownaudio.Core;

namespace OwnaudioNET.Engine;

/// <summary>
/// Registers the Rust-backed <see cref="RustAudioEngine"/> as the creator for
/// <see cref="Ownaudio.Core.AudioEngineFactory"/> and the Rust-backed
/// <see cref="RustNativeDecoder"/> as the native decoder for
/// <see cref="Ownaudio.Decoders.AudioDecoderFactory"/> at module load time, so callers of the
/// low-level factories receive the cpal-driven engine and the Symphonia decoder without any
/// setup code. This mirrors the registration the deleted PortAudio/MiniAudio layer performed,
/// now pointing at the Rust engine and decoder.
/// </summary>
internal static class RustAudioEngineRegistrar
{
    /// <summary>
    /// Runs automatically when the assembly is loaded and wires the Rust engine and decoder
    /// creators into the core factories.
    /// </summary>
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Deliberate: auto-registers the Rust engine and decoder into the core " +
            "factories on assembly load so consumers of the low-level factories need no setup code.")]
    internal static void Register()
    {
        Ownaudio.Core.AudioEngineFactory.Register(() => new RustAudioEngine());
        Ownaudio.Decoders.AudioDecoderFactory.RegisterNativeDecoder(
            (path, sampleRate, channels) => new RustNativeDecoder(path, sampleRate, channels));
    }
}
