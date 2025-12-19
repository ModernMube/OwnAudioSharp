using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;

namespace Ownaudio.Native.Utils
{
    /// <summary>
    /// Cross-platform native library loader with runtime identifier (RID) support.
    /// Uses .NET's built-in NativeLibrary API for reliable cross-platform loading.
    /// </summary>
    internal sealed class LibraryLoader : IDisposable
    {
        /// <summary>
        /// Handle to the loaded native library.
        /// </summary>
        private readonly IntPtr _handle;

        /// <summary>
        /// Path to the loaded library file.
        /// </summary>
        private readonly string _libraryPath;

        /// <summary>
        /// Indicates whether the loader has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Gets the handle to the loaded library.
        /// </summary>
        public IntPtr Handle => _handle;

        /// <summary>
        /// Gets the path to the loaded library.
        /// </summary>
        public string LibraryPath => _libraryPath;

        /// <summary>
        /// Loads a native library with the specified name.
        /// Automatically resolves the platform-specific path.
        /// </summary>
        /// <param name="libraryName">Name of the library (without extension or lib prefix)</param>
        public LibraryLoader(string libraryName)
        {
            var assembly = Assembly.GetExecutingAssembly();

            if (NativeLibrary.TryLoad(libraryName, assembly, null, out _handle))
            {
                _libraryPath = libraryName; // Store the name since .NET handled resolution
                return;
            }

            _libraryPath = ResolvePlatformLibraryPath(libraryName);

            if (!File.Exists(_libraryPath))
            {
                throw new DllNotFoundException(
                    $"Native library not found: {libraryName}\n" +
                    $"Attempted paths:\n" +
                    $"  1. .NET runtime resolver (runtimes/{GetCurrentRuntimeIdentifier()}/native/)\n" +
                    $"  2. Manual resolution: {_libraryPath}\n" +
                    $"Platform: {GetCurrentRuntimeIdentifier()}");
            }

            if (!NativeLibrary.TryLoad(_libraryPath, out _handle))
            {
                throw new DllNotFoundException(
                    $"Failed to load native library: {_libraryPath}\n" +
                    $"Platform: {GetCurrentRuntimeIdentifier()}");
            }
        }

        /// <summary>
        /// Loads a function pointer from the loaded library.
        /// </summary>
        /// <typeparam name="TDelegate">The delegate type for the function</typeparam>
        /// <param name="functionName">The name of the function to load</param>
        /// <returns>A delegate to the loaded function</returns>
        public TDelegate LoadFunc<TDelegate>(string functionName) where TDelegate : Delegate
        {
            if (!NativeLibrary.TryGetExport(_handle, functionName, out IntPtr funcPtr))
            {
                throw new EntryPointNotFoundException(
                    $"Function '{functionName}' not found in library: {_libraryPath}");
            }

            return Marshal.GetDelegateForFunctionPointer<TDelegate>(funcPtr);
        }

        /// <summary>
        /// Tries to load a function pointer from the loaded library.
        /// Returns null if the function is not found instead of throwing an exception.
        /// </summary>
        /// <typeparam name="TDelegate">The delegate type for the function</typeparam>
        /// <param name="functionName">The name of the function to load</param>
        /// <returns>A delegate to the loaded function, or null if not found</returns>
        public TDelegate? TryLoadFunc<TDelegate>(string functionName) where TDelegate : Delegate
        {
            if (!NativeLibrary.TryGetExport(_handle, functionName, out IntPtr funcPtr))
            {
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<TDelegate>(funcPtr);
        }

        /// <summary>
        /// Resolves the platform-specific library path based on runtime identifier.
        /// Checks bundled libraries first, then system-installed libraries.
        /// </summary>
        private static string ResolvePlatformLibraryPath(string libraryName)
        {
            string rid = GetCurrentRuntimeIdentifier();
            string fileName = GetLibraryFileName(libraryName);
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;

            // Build search paths list
            var searchPaths = new List<string>
            {
                // 1. Standard RID structure in application directory
                Path.Combine(baseDir, "runtimes", rid, "native", fileName),
                // 2. Flat structure in output directory
                Path.Combine(baseDir, fileName)
            };

            // 3. Add system-specific paths for PortAudio
            if (libraryName.Contains("portaudio"))
            {
                searchPaths.AddRange(GetSystemPortAudioPaths(fileName));
            }

            // 4. Try system library path (works on Linux/macOS with LD_LIBRARY_PATH)
            searchPaths.Add(fileName);

            // Search for the library
            foreach (string path in searchPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Return the most likely path (will fail later with better error message)
            return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        }

        /// <summary>
        /// Gets system-specific installation paths for PortAudio library.
        /// </summary>
        private static IEnumerable<string> GetSystemPortAudioPaths(string fileName)
        {
            var paths = new List<string>();
            var arch = RuntimeInformation.ProcessArchitecture;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS Homebrew paths
                if (arch == Architecture.Arm64)
                {
                    // Apple Silicon (M1/M2/M3) - Homebrew installs to /opt/homebrew
                    paths.Add(Path.Combine("/opt", "homebrew", "opt", "portaudio", "lib", fileName));
                    paths.Add(Path.Combine("/opt", "homebrew", "lib", fileName));
                }
                else if (arch == Architecture.X64)
                {
                    // Intel Mac - Homebrew installs to /usr/local
                    paths.Add(Path.Combine("/usr", "local", "opt", "portaudio", "lib", fileName));
                    paths.Add(Path.Combine("/usr", "local", "lib", fileName));
                }

                // Common macOS paths
                paths.Add(Path.Combine("/usr", "lib", fileName));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Linux system library paths (Debian/Ubuntu/Fedora/Arch style)
                switch (arch)
                {
                    case Architecture.Arm:
                        paths.Add(Path.Combine("/usr", "lib", "arm-linux-gnueabihf", $"{fileName}.2"));
                        paths.Add(Path.Combine("/usr", "lib", "arm-linux-gnueabihf", fileName));
                        break;
                    case Architecture.Arm64:
                        paths.Add(Path.Combine("/usr", "lib", "aarch64-linux-gnu", $"{fileName}.2"));
                        paths.Add(Path.Combine("/usr", "lib", "aarch64-linux-gnu", fileName));
                        break;
                    case Architecture.X64:
                        paths.Add(Path.Combine("/usr", "lib", "x86_64-linux-gnu", $"{fileName}.2"));
                        paths.Add(Path.Combine("/usr", "lib", "x86_64-linux-gnu", fileName));
                        paths.Add(Path.Combine("/usr", "lib64", $"{fileName}.2"));
                        paths.Add(Path.Combine("/usr", "lib64", fileName));
                        break;
                    case Architecture.X86:
                        paths.Add(Path.Combine("/usr", "lib", "i386-linux-gnu", $"{fileName}.2"));
                        paths.Add(Path.Combine("/usr", "lib", "i386-linux-gnu", fileName));
                        break;
                }

                // Generic Linux paths
                paths.Add(Path.Combine("/usr", "lib", $"{fileName}.2"));
                paths.Add(Path.Combine("/usr", "lib", fileName));
                paths.Add(Path.Combine("/usr", "local", "lib", fileName));
            }

            return paths;
        }

        /// <summary>
        /// Gets the platform-specific library file name.
        /// </summary>
        private static string GetLibraryFileName(string libraryName)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return $"{libraryName}.dll";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return libraryName.StartsWith("lib") ? $"{libraryName}.dylib" : $"lib{libraryName}.dylib";
            }
            else // Linux, Android, etc.
            {
                return libraryName.StartsWith("lib") ? $"{libraryName}.so" : $"lib{libraryName}.so";
            }
        }

        /// <summary>
        /// Gets the current runtime identifier (RID).
        /// </summary>
        private static string GetCurrentRuntimeIdentifier()
        {
            // Determine OS
            string os;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                os = "win";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                os = "osx";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (OperatingSystem.IsAndroid())
                    os = "android";
                else
                    os = "linux";
            }
            else if (OperatingSystem.IsIOS())
                os = "iOS";
            else
                throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");

            // Determine architecture
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}")
            };

            return $"{os}-{arch}";
        }

        /// <summary>
        /// Disposes the library loader and frees the native library handle.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            if (_handle != IntPtr.Zero)
            {
                NativeLibrary.Free(_handle);
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer to ensure native resources are freed.
        /// </summary>
        ~LibraryLoader()
        {
            Dispose();
        }
    }
}
