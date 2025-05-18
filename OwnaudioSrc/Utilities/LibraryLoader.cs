using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Utilities;

/// <summary>
/// Native library loader
/// </summary>
internal sealed class LibraryLoader : IDisposable
{
    private readonly IntPtr _handle;
    private bool _disposed;

    /// <summary>
    /// Load native library
    /// </summary>
    /// <param name="libraryName">Native library path and name</param>
    /// <exception cref="NotSupportedException"></exception>
    public LibraryLoader(string libraryName)
    {
        Ensure.NotNull(libraryName, nameof(libraryName));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || 
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            _handle = NativeLibrary.Load(libraryName);
        }
        else
            throw new NotSupportedException("Platform is not supported.");

        Ensure.That<Exception>(_handle != IntPtr.Zero, $"Could not load native libary: {libraryName}.");
    }

    /// <summary>
    /// Load native library function
    /// </summary>
    /// <typeparam name="TDelegate"></typeparam>
    /// <param name="name">function name</param>
    /// <returns></returns>
    public TDelegate LoadFunc<TDelegate>(string name)
    {
        var ptr = NativeLibrary.GetExport(_handle, name);
        
        Ensure.That<Exception>(ptr != IntPtr.Zero, $"Could not load function name: {name}.");

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(ptr);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        NativeLibrary.Free(_handle);

        _disposed = true;
    }
}
