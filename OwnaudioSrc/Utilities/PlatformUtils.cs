using System;
using System.IO;
#if IOS
using Foundation; // NSBundle és NSSearchPath eléréséhez
using System.Linq;  // FirstOrDefault() eléréséhez
#endif

namespace Ownaudio.Utilities
{
    internal static class PlatformUtils
    {
        /// <summary>
        /// Specifies the program directory, depending on the system
        /// </summary>
        /// <returns></returns>
        public static string? tGetAppSpecificBasePah()
        {
            string? path = null;
#if ANDROID
            path = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
#elif IOS
            // Ajánlott hely belső adatoknak: Library/Application Support/AppName
            string appSupportPath = NSSearchPath.GetDirectories(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User).FirstOrDefault();

            if (!string.IsNullOrEmpty(appSupportPath))
            {
                // Az alkalmazás nevének lekérése a bundle-ből (CFBundleName)
                string appName = NSBundle.MainBundle.InfoDictionary?["CFBundleName"]?.ToString() ?? "MyApp"; // Használj egy alapértelmezett nevet, ha nem található
                path = Path.Combine(appSupportPath, appName);

                // Győződjünk meg róla, hogy a könyvtár létezik
                if (!Directory.Exists(path))
                {
                    try
                    {
                        Directory.CreateDirectory(path);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[HIBA] IOS: Nem sikerült létrehozni a könyvtárat '{path}': {ex.Message}");
                        path = null; // Hiba esetén null értékkel térünk vissza
                    }
                }
            }
            else
            {
                Console.WriteLine("[HIBA] IOS: Nem található az Application Support könyvtár. Visszaesés a Documents könyvtárra.");
                // Végső esetben visszaeshetünk a Documents könyvtárra, bár az Application Support jobb lenne.
                path = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal); // Ez a Documents
            }
#else       // Windows, macOS, Linux (desktop)
            path = AppContext.BaseDirectory;
#endif

            if (string.IsNullOrEmpty(path))
            {
                Console.WriteLine($"[ERROR] The application-specific base path could not be determined on the current platform.");
            }
            else
            {
                Console.WriteLine($"[INFO] Defined application-specific base path: {path}");
            }

            return path;
        }

#if IOS
    ++public static class IOSBundleCopier
    {
        // sourceFolderNameInBundle: A másolandó mappa neve az alkalmazáscsomagban (pl. "runtimes").
        // targetSubFolderInAppData: A célmappa neve az alkalmazás írható területén belül (az app-specifikus base path alatt).
        // overwrite: Felülírja-e a meglévő fájlokat.
        public static void CopyBundleFolderToAppData(string sourceFolderNameInBundle, string targetSubFolderInAppData, bool overwrite = false)
        {
            string? appSpecificDataPath = PlatformUtils.GetAppSpecificBasePath();
            if (string.IsNullOrEmpty(appSpecificDataPath))
            {
                Console.WriteLine("[ERROR] IOSBundleCopier: Failed to retrieve application-specific data path. Copy aborted.");
                return;
            }

            string targetFullPath = Path.Combine(appSpecificDataPath, targetSubFolderInAppData);

            if (Directory.Exists(targetFullPath) && !overwrite)
            {
                Console.WriteLine($"[INFO] IOSBundleCopier: The target directory '{targetFullPath}' already exists. Copying omitted (overwrite=false).");
                return;
            }

            if (Directory.Exists(targetFullPath) && overwrite)
            {
                Console.WriteLine($"[INFO] IOSBundleCopier: The target directory '{targetFullPath}' exists and will be overwritten.");
                Directory.Delete(targetFullPath, true);
            }

            if (!Directory.Exists(Path.GetDirectoryName(targetFullPath))) {
                 Directory.CreateDirectory(Path.GetDirectoryName(targetFullPath));
            }
            Directory.CreateDirectory(targetFullPath);

            string bundlePath = NSBundle.MainBundle.BundlePath;
            string sourcePathInBundle = Path.Combine(bundlePath, sourceFolderNameInBundle);

            if (!Directory.Exists(sourcePathInBundle))
            {
                Console.WriteLine($"[ERROR] IOSBundleCopier: The source directory '{sourcePathInBundle}' not found in the application package.");
                return;
            }

            Console.WriteLine($"[INFO] IOSBundleCopier: Copy starts: '{sourcePathInBundle}' (bundle) -> '{targetFullPath}' (app data)");
            CopyDirectoryRecursive(sourcePathInBundle, targetFullPath);
            Console.WriteLine($"[INFO] IOSBundleCopier: Copying completed: '{sourcePathInBundle}' -> '{targetFullPath}'");
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
        {
            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                try
                {
                    File.Copy(file, destFile, true); // true = felülírás
                    // Console.WriteLine($"[DEBUG] IOSBundleCopier: File copied: {destFile}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HIBA] IOSBundleCopier: ERROR '{file}' when copying a file to: '{destFile}': {ex.Message}");
                }
            }

            foreach (string folder in Directory.GetDirectories(sourceDir))
            {
                string destFolder = Path.Combine(destinationDir, Path.GetFileName(folder));
                CopyDirectoryRecursive(folder, destFolder);
            }
        }
    }
#endif
    }
}
