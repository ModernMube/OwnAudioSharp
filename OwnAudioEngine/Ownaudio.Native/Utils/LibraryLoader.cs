
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
        /// Operating-system handle to the loaded native shared library.
        /// Passed to <see cref="NativeLibrary.TryGetExport"/> for symbol lookups
        /// and released via <see cref="NativeLibrary.Free"/> on disposal.
        /// </summary>
        private readonly IntPtr _handle;

        /// <summary>
        /// Resolved filesystem path (or bare library name when the .NET runtime performed
        /// the path resolution) of the loaded library. Used in diagnostic error messages.
        /// </summary>
        private readonly string _libraryPath;

        /// <summary>
        /// Indicates whether <see cref="Dispose"/> has already been called for this instance.
        /// Guards against double-free of the native library handle.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Gets the native OS handle to the loaded library.
        /// Store it for manual P/Invoke scenarios or pass to <see cref="NativeLibrary.TryGetExport"/>.
        /// </summary>
        public IntPtr Handle => _handle;

        /// <summary>
        /// Gets the filesystem path or bare name used to successfully load the library.
        /// Useful for diagnostic logging to confirm which binary was loaded at runtime.
        /// </summary>
        public string LibraryPath => _libraryPath;

        /// <summary>
        /// Loads a native library identified by <paramref name="libraryName"/>.
        /// First attempts the .NET runtime's built-in resolver (which handles
        /// runtimes/&lt;rid&gt;/native/ directories automatically), then falls back to a
        /// manual platform-specific path resolution strategy covering system-installed libraries.
        /// </summary>
        /// <param name="libraryName">Name of the library without platform prefix or extension.</param>
        public LibraryLoader(string libraryName)
        {
            var assembly = Assembly.GetExecutingAssembly();

            if (NativeLibrary.TryLoad(libraryName, assembly, null, out _handle))
            {
                _libraryPath = libraryName;
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
        /// Loads a function from the library and returns it as a strongly-typed delegate.
        /// This method requires dynamic code generation and is therefore not compatible with NativeAOT.
        /// Prefer <see cref="GetExport"/> combined with a <c>delegate* unmanaged[Cdecl]</c> cast
        /// for AOT-compatible interop.
        /// </summary>
        /// <typeparam name="TDelegate">The delegate type corresponding to the native function signature.</typeparam>
        /// <param name="functionName">The exact name of the exported native symbol.</param>
        /// <returns>A managed delegate bound to the native function pointer.</returns>
        [RequiresDynamicCode("Delegate marshalling requires dynamic code generation.")]
        [RequiresUnreferencedCode("Delegate types must be preserved for native interop.")]
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
        /// Tries to load a function from the library and returns it as a strongly-typed delegate.
        /// Returns <see langword="null"/> when the symbol is not found instead of throwing.
        /// This method requires dynamic code generation and is therefore not compatible with NativeAOT.
        /// Prefer <see cref="TryGetExport"/> combined with a <c>delegate* unmanaged[Cdecl]</c> cast
        /// for AOT-compatible interop.
        /// </summary>
        /// <typeparam name="TDelegate">The delegate type corresponding to the native function signature.</typeparam>
        /// <param name="functionName">The exact name of the exported native symbol.</param>
        /// <returns>
        /// A managed delegate bound to the native function pointer,
        /// or <see langword="null"/> when the symbol is not found.
        /// </returns>
        [RequiresDynamicCode("Delegate marshalling requires dynamic code generation.")]
        [RequiresUnreferencedCode("Delegate types must be preserved for native interop.")]
        public TDelegate? TryLoadFunc<TDelegate>(string functionName) where TDelegate : Delegate
        {
            if (!NativeLibrary.TryGetExport(_handle, functionName, out IntPtr funcPtr))
            {
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer<TDelegate>(funcPtr);
        }

        /// <summary>
        /// Returns the raw function pointer address for the exported symbol named
        /// <paramref name="functionName"/> from the loaded library.
        /// Store the returned <see cref="IntPtr"/> in a
        /// <c>delegate* unmanaged[Cdecl]&lt;...&gt;</c> field for AOT-safe,
        /// zero-overhead native calls with no dynamic code generation.
        /// </summary>
        /// <param name="functionName">The exact name of the exported native symbol.</param>
        /// <returns>The function address as a non-zero <see cref="IntPtr"/>.</returns>
        /// <exception cref="EntryPointNotFoundException">
        /// Thrown when <paramref name="functionName"/> is not exported by the loaded library.
        /// </exception>
        public IntPtr GetExport(string functionName)
        {
            if (!NativeLibrary.TryGetExport(_handle, functionName, out IntPtr funcPtr))
            {
                throw new EntryPointNotFoundException(
                    $"Function '{functionName}' not found in library: {_libraryPath}");
            }

            return funcPtr;
        }

        /// <summary>
        /// Attempts to return the raw function pointer address for the exported symbol named
        /// <paramref name="functionName"/> from the loaded library.
        /// Unlike <see cref="GetExport"/>, this method does not throw when the symbol is absent.
        /// Callers should treat a return value of <see cref="IntPtr.Zero"/> as "not supported".
        /// </summary>
        /// <param name="functionName">The exact name of the exported native symbol.</param>
        /// <returns>
        /// The function address as a non-zero <see cref="IntPtr"/>, or
        /// <see cref="IntPtr.Zero"/> when the symbol is not found.
        /// </returns>
        public IntPtr TryGetExport(string functionName)
        {
            NativeLibrary.TryGetExport(_handle, functionName, out IntPtr funcPtr);
            return funcPtr;
        }

        /// <summary>
        /// Searches for the platform-specific library file path using a prioritised list
        /// of candidate locations: first the application's RID-qualified runtimes directory,
        /// then the application base directory, and finally system-level installation paths.
        /// </summary>
        private static string ResolvePlatformLibraryPath(string libraryName)
        {
            string rid = GetCurrentRuntimeIdentifier();
            string fileName = GetLibraryFileName(libraryName);
            string baseDir = AppContext.BaseDirectory;

            var searchPaths = new List<string>
            {
                Path.Combine(baseDir, "runtimes", rid, "native", fileName),
                Path.Combine(baseDir, fileName)
            };

            if (libraryName.Contains("portaudio"))
            {
                searchPaths.AddRange(GetSystemPortAudioPaths(fileName));
            }

            searchPaths.Add(fileName);

            foreach (string path in searchPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        }

        /// <summary>
        /// Returns a prioritised list of well-known system installation paths for the PortAudio
        /// shared library on the current operating system and CPU architecture.
        /// Covers Apple Silicon and Intel macOS (Homebrew) and common Linux multiarch directories.
        /// </summary>
        private static IEnumerable<string> GetSystemPortAudioPaths(string fileName)
        {
            var paths = new List<string>();
            var arch = RuntimeInformation.ProcessArchitecture;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (arch == Architecture.Arm64)
                {
                    paths.Add(Path.Combine("/opt", "homebrew", "opt", "portaudio", "lib", fileName));
                    paths.Add(Path.Combine("/opt", "homebrew", "lib", fileName));
                }
                else if (arch == Architecture.X64)
                {
                    paths.Add(Path.Combine("/usr", "local", "opt", "portaudio", "lib", fileName));
                    paths.Add(Path.Combine("/usr", "local", "lib", fileName));
                }

                paths.Add(Path.Combine("/usr", "lib", fileName));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
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

                paths.Add(Path.Combine("/usr", "lib", $"{fileName}.2"));
                paths.Add(Path.Combine("/usr", "lib", fileName));
                paths.Add(Path.Combine("/usr", "local", "lib", fileName));
            }

            return paths;
        }

        /// <summary>
        /// Returns the platform-specific file name for a native library identified by
        /// <paramref name="libraryName"/>, including the correct prefix and extension.
        /// On Windows this appends <c>.dll</c>; on macOS adds <c>lib</c> prefix and <c>.dylib</c>;
        /// on Linux and Android adds <c>lib</c> prefix and <c>.so</c>.
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
            else
            {
                return libraryName.StartsWith("lib") ? $"{libraryName}.so" : $"lib{libraryName}.so";
            }
        }

        /// <summary>
        /// Determines the .NET runtime identifier (RID) for the current operating system
        /// and CPU architecture combination (e.g., <c>osx-arm64</c>, <c>win-x64</c>).
        /// Used to locate bundled native libraries in the standard runtimes/&lt;rid&gt;/native/ layout.
        /// </summary>
        private static string GetCurrentRuntimeIdentifier()
        {
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
        /// Releases the native library handle, allowing the OS to unload the shared library
        /// once no further references remain.
        /// Calling <see cref="Dispose"/> more than once is safe and has no additional effect.
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
        /// Finalizer that ensures the native library handle is freed even when
        /// <see cref="Dispose"/> was not called explicitly by the consumer.
        /// Prefer using a <see langword="using"/> statement to ensure deterministic cleanup.
        /// </summary>
        ~LibraryLoader()
        {
            Dispose();
        }
    }
}
