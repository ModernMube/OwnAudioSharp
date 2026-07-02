using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ownaudio.EngineTest;

[TestClass]
public static class TestAssemblySetup
{
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context)
    {
        // Force-load the OwnaudioNET assembly so its [ModuleInitializer]
        // (RustAudioEngineRegistrar) runs and registers the Rust engine creator with
        // AudioEngineFactory. Without this, macOS (and Linux) do not load the assembly
        // automatically, leaving _creator null.
        var _ = typeof(OwnaudioNET.OwnaudioNet);
    }
}
