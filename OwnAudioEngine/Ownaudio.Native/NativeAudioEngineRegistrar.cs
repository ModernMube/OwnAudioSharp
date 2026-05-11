using System.Runtime.CompilerServices;
using Ownaudio.Core;
using Ownaudio.Decoders;
using Ownaudio.Native.Decoders;

namespace Ownaudio.Native;

/// <summary>
/// Registers the <see cref="NativeAudioEngine"/> and <see cref="MaDecoder"/> factories
/// with their respective factory classes the moment this assembly is loaded.
/// The <c>[ModuleInitializer]</c> attribute guarantees automatic execution without any
/// explicit caller setup, making all registrations AOT-transparent and reflection-free.
/// </summary>
internal static class NativeAudioEngineRegistrar
{
    #region Module Initializer

    /// <summary>
    /// Runs automatically when the assembly is first loaded; wires up engine and decoder factories.
    /// </summary>
    [ModuleInitializer]
    internal static void Register()
    {
        AudioEngineFactory.Register(static () => new NativeAudioEngine());
        AudioDecoderFactory.RegisterNativeDecoder(static (path, sr, ch) => new MaDecoder(path, sr, ch));
    }

    #endregion
}
