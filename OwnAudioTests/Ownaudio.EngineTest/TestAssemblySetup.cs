using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Native;

namespace Ownaudio.EngineTest;

[TestClass]
public static class TestAssemblySetup
{
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context)
    {
        // Force-load Ownaudio.Native so its [ModuleInitializer] runs and registers
        // the engine factory with AudioEngineFactory. Without this, macOS (and Linux)
        // do not load the assembly automatically, leaving _creator null.
        var _ = typeof(NativeAudioEngine);
    }
}
