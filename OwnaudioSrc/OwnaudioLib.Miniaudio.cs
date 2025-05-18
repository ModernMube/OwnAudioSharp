using System;
using System.Collections.Generic;

using Ownaudio.Bindings.Miniaudio;
using Ownaudio.Exceptions;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using Ownaudio.Engines;
using System.IO;
using System.Runtime.InteropServices;
using static Ownaudio.Bindings.Miniaudio.MaBinding;
using Ownaudio.MiniAudio;
using System.Linq;

namespace Ownaudio;

/// <summary>
/// Functions to retrieve, configure and manage the current Ownaudio environment, 
/// which affects the entire directory configuration.
/// </summary>
public static partial class OwnAudio
{
    private static IntPtr _context = IntPtr.Zero;

    /// <summary>
    /// Terminates processes and frees memory.
    /// </summary>
    public static void FreeMa()
    {
        if (IsMiniAudioInitialized && _context != IntPtr.Zero)
        {
            MaBinding.ma_context_uninit(_context);
            Marshal.FreeHGlobal(_context);
            _context = IntPtr.Zero;
        }

        IsMiniAudioInitialized = false;
    }
    
    /// <summary>
    /// Initialize and register MiniAudio functions by providing the path to MiniAudio's native library. 
    /// Leave the path parameter blank to use the system directory. 
    /// Exits if already initialized.
    /// </summary>
    /// <param name="miniAudioPath">Path to native miniaudio directory, eg miniaudio.dll, libminiaudio.so, libminiaudio.dylib.</param>
    /// <param name="hostType">Audio API type</param>
    /// <exception cref="OwnaudioException">Throws an exception if no output device is available.</exception>
    private static void InitializeMa(string? miniAudioPath = default, OwnAudioEngine.EngineHostType hostType = OwnAudioEngine.EngineHostType.None)
    {
        if (IsMiniAudioInitialized || string.IsNullOrEmpty(miniAudioPath))
        {
            return;
        }
        
        MaBinding.InitializeBindings(new LibraryLoader(miniAudioPath));

        IsMiniAudioInitialized = false;
        _outputDevices.Clear();
        _inputDevices.Clear();

        var engine = new MiniAudioEngine();
        engine.UpdateDevicesInfo();

        _outputDevices = engine.PlaybackDevices
            .Select((dev, index) => new AudioDevice(index, dev.Name, 2, 0, 0.02, 0, 0, 0, 44100))
            .ToList();

        _inputDevices = engine.CaptureDevices
            .Select((dev, index) => new AudioDevice(index, dev.Name, 0, 1, 0, 0, 0.02, 0, 44100))
            .ToList();

        if (_outputDevices.Count > 0)
        {
            IsMiniAudioInitialized = true;
            _defaultOutputDevice = _outputDevices[0];
        }

        if (_inputDevices.Count > 0)
        {
            _defaultInputDevice = _inputDevices[0];
        }
        
        MiniAudioPath = miniAudioPath;        
    }
}
