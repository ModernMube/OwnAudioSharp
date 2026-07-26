using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ownaudio.EngineTest;

[TestClass]
public static class TestAssemblySetup
{
    /// <summary>
    /// Runs OwnaudioNET's module initializer (RustAudioEngineRegistrar) so the Rust engine creator
    /// lands in AudioEngineFactory before the first test. macOS and Linux don't load the assembly on
    /// their own, and a bare typeof() isn't enough: the reference gets optimised away in Release and
    /// the module .cctor never fires, so every factory based test dies on a null creator.
    /// </summary>
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context)
    {
        RuntimeHelpers.RunModuleConstructor(typeof(OwnaudioNET.OwnaudioNet).Module.ModuleHandle);
    }
}
