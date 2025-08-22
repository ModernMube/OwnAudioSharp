using System;
using System.Diagnostics;
#if IOS
using Foundation; // For NSBundle and NSSearchPath access
using System.Linq;  // For FirstOrDefault() access
#endif

namespace Ownaudio.Utilities
{
    /// <summary>
    /// Provides platform-specific utility functions for cross-platform application development.
    /// Handles path resolution and file operations that vary across different operating systems.
    /// </summary>
    internal static class PlatformUtils
    {
        /// <summary>
        /// Determines the application-specific base directory path depending on the target platform.
        /// </summary>
        /// <returns>
        /// The platform-appropriate application base path, or null if the path could not be determined.
        /// </returns>
        /// <remarks>
        /// This method provides platform-specific path resolution:
        /// 
        /// **Android**: Returns the Personal folder (app's private data directory)
        /// 
        /// **iOS**: Returns Library/Application Support/AppName directory
        /// - Attempts to get the Application Support directory from iOS system paths
        /// - Creates a subdirectory using the app's bundle name (CFBundleName)
        /// - Automatically creates the directory if it doesn't exist
        /// - Falls back to Documents directory if Application Support is unavailable
        /// - Uses "MyApp" as default if bundle name cannot be determined
        /// 
        /// **Desktop platforms** (Windows, macOS, Linux): Returns AppContext.BaseDirectory
        /// 
        /// The method includes comprehensive error handling and logging for debugging purposes.
        /// </remarks>
        public static string? GetAppSpecificBasePath()
        {
            string? path = null;
#if ANDROID
            path = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
#elif IOS
            // Recommended location for internal data: Library/Application Support/AppName
            string appSupportPath = NSSearchPath.GetDirectories(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User).FirstOrDefault();

            if (!string.IsNullOrEmpty(appSupportPath))
            {
                // Get the application name from the bundle (CFBundleName)
                string appName = NSBundle.MainBundle.InfoDictionary?["CFBundleName"]?.ToString() ?? "MyApp"; // Use a default name if not found
                path = Path.Combine(appSupportPath, appName);

                // Ensure the directory exists
                if (!Directory.Exists(path))
                {
                    try
                    {
                        Directory.CreateDirectory(path);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] iOS: Failed to create directory '{path}': {ex.Message}");
                        path = null; // Return null in case of error
                    }
                }
            }
            else
            {
                Debug.WriteLine("[ERROR] iOS: Application Support directory not found. Falling back to Documents directory.");
                // As a last resort, we can fall back to the Documents directory, although Application Support would be better.
                path = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal); // This is the Documents directory
            }
#else       // Windows, macOS, Linux (desktop)
            path = AppContext.BaseDirectory;
#endif

            if (string.IsNullOrEmpty(path))
            {
                Debug.WriteLine($"[ERROR] The application-specific base path could not be determined on the current platform.");
            }
            else
            {
                Debug.WriteLine($"[INFO] Defined application-specific base path: {path}");
            }

            return path;
        }

#if IOS
        /// <summary>
        /// Provides utilities for copying files and directories from the iOS application bundle to writable application data directories.
        /// This is essential for iOS applications that need to extract bundled resources to writable locations.
        /// </summary>
        public static class IOSBundleCopier
        {
            /// <summary>
            /// Copies a folder from the iOS application bundle to the application's writable data directory.
            /// </summary>
            /// <param name="sourceFolderNameInBundle">The name of the folder to copy from the application bundle (e.g., "runtimes").</param>
            /// <param name="targetSubFolderInAppData">The target folder name within the application's writable area (under the app-specific base path).</param>
            /// <param name="overwrite">Whether to overwrite existing files and directories (default: false).</param>
            /// <remarks>
            /// This method performs the following operations:
            /// - Retrieves the application-specific data path using <see cref="PlatformUtils.GetAppSpecificBasePath"/>
            /// - Constructs the full target path by combining the app data path with the target subfolder
            /// - Handles existing directories based on the overwrite parameter
            /// - Locates the source folder within the application bundle
            /// - Performs recursive directory copying using <see cref="CopyDirectoryRecursive"/>
            /// - Provides comprehensive logging for debugging and monitoring
            /// 
            /// **Error Handling:**
            /// - Aborts if the application data path cannot be determined
            /// - Skips copying if target exists and overwrite is false
            /// - Reports errors if source directory is not found in bundle
            /// - Continues copying other files if individual file copy operations fail
            /// 
            /// **Use Cases:**
            /// - Extracting native libraries from bundle to writable locations
            /// - Copying configuration files that need to be modified at runtime
            /// - Setting up initial application data from bundled templates
            /// </remarks>
            public static void CopyBundleFolderToAppData(string sourceFolderNameInBundle, string targetSubFolderInAppData, bool overwrite = false)
            {
                string? appSpecificDataPath = PlatformUtils.GetAppSpecificBasePath();
                if (string.IsNullOrEmpty(appSpecificDataPath))
                {
                    Debug.WriteLine("[ERROR] IOSBundleCopier: Failed to retrieve application-specific data path. Copy aborted.");
                    return;
                }

                string targetFullPath = Path.Combine(appSpecificDataPath, targetSubFolderInAppData);

                if (Directory.Exists(targetFullPath) && !overwrite)
                {
                    Debug.WriteLine($"[INFO] IOSBundleCopier: The target directory '{targetFullPath}' already exists. Copying omitted (overwrite=false).");
                    return;
                }

                if (Directory.Exists(targetFullPath) && overwrite)
                {
                    Debug.WriteLine($"[INFO] IOSBundleCopier: The target directory '{targetFullPath}' exists and will be overwritten.");
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
                    Debug.WriteLine($"[ERROR] IOSBundleCopier: The source directory '{sourcePathInBundle}' not found in the application package.");
                    return;
                }

                Debug.WriteLine($"[INFO] IOSBundleCopier: Copy starts: '{sourcePathInBundle}' (bundle) -> '{targetFullPath}' (app data)");
                CopyDirectoryRecursive(sourcePathInBundle, targetFullPath);
                Debug.WriteLine($"[INFO] IOSBundleCopier: Copying completed: '{sourcePathInBundle}' -> '{targetFullPath}'");
            }

            /// <summary>
            /// Recursively copies all files and subdirectories from a source directory to a destination directory.
            /// </summary>
            /// <param name="sourceDir">The path to the source directory to copy from.</param>
            /// <param name="destinationDir">The path to the destination directory to copy to.</param>
            /// <remarks>
            /// This method performs a deep copy operation:
            /// 
            /// **Directory Handling:**
            /// - Creates the destination directory if it doesn't exist
            /// - Recursively processes all subdirectories
            /// - Maintains the original directory structure
            /// 
            /// **File Copying:**
            /// - Copies all files from the source directory
            /// - Uses File.Copy with overwrite enabled (true parameter)
            /// - Preserves original file names
            /// - Includes error handling for individual file copy failures
            /// 
            /// **Error Resilience:**
            /// - Continues copying other files even if individual copy operations fail
            /// - Logs errors for failed file operations without stopping the entire process
            /// - Uses try-catch blocks to handle file access permissions and other I/O issues
            /// 
            /// This method is designed to be robust and continue operation even when some
            /// files cannot be copied due to permissions or other system-level restrictions.
            /// </remarks>
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
                        File.Copy(file, destFile, true); // true = overwrite
                        // Console.WriteLine($"[DEBUG] IOSBundleCopier: File copied: {destFile}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ERROR] IOSBundleCopier: Error copying file '{file}' to '{destFile}': {ex.Message}");
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