using System.Runtime.CompilerServices;
using Ownaudio.Core;

namespace OwnaudioNET.Engine;

/// <summary>
/// Wires the Rust engine and the Symphonia decoder into the core factories at module load, so the low level
/// factories hand back the native stuff with no setup code. Same trick the old PortAudio/MiniAudio layer did.
/// </summary>
internal static class RustAudioEngineRegistrar
{
    /// <summary>
    /// Runs on assembly load and registers both creators.
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
