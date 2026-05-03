using System.Runtime.CompilerServices;
using Ownaudio.Core;

namespace Ownaudio.Native;

/// <summary>
/// Registers the <see cref="NativeAudioEngine"/> factory with <see cref="AudioEngineFactory"/>
/// the moment this assembly is loaded. The <c>[ModuleInitializer]</c> attribute guarantees
/// automatic execution without any explicit caller setup, making the registration
/// AOT-transparent and reflection-free.
/// </summary>
internal static class NativeAudioEngineRegistrar
{
    #region Module Initializer

    /// <summary>
    /// Runs automatically when the assembly is first loaded; wires up the engine creator.
    /// </summary>
    [ModuleInitializer]
    internal static void Register()
    {
        AudioEngineFactory.Register(static () => new NativeAudioEngine());
    }

    #endregion
}
