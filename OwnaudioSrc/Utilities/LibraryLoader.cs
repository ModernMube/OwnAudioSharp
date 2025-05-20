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

        try
        {
            _handle = NativeLibrary.Load(libraryName);
        }
        catch(DllNotFoundException ex)
        {
            Console.WriteLine($"[ERROR] DllNotFoundException when loading 'miniaudio': {ex.Message}");
        }
        catch (Exception ex)
        {
            throw new NotSupportedException($"Platform is not supported. ERROR: {ex.Message}");
        }

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
