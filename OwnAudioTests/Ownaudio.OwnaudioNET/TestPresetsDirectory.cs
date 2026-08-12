using System;
using System.IO;
using System.Runtime.CompilerServices;
using OwnaudioNET.Effects.SmartMaster;

namespace Ownaudio.Test.OwnaudioNET
{
    /// <summary>
    /// Keeps the preset tests off the machine's own presets - each run gets an empty folder.
    /// </summary>
    internal static class TestPresetsDirectory
    {
        [ModuleInitializer]
        internal static void Redirect()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ownaudio-presets-" + Guid.NewGuid().ToString("N"));
            SmartMasterEffect.PresetsDirectoryOverride = dir;

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                catch (IOException) { }
            };
        }
    }
}
