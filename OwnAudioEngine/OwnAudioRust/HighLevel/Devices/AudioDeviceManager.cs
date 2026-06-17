using System;
using System.Collections.Generic;
using Ownaudio.Safe;
using Ownaudio.Safe.Exceptions;
using AudioDeviceException = Ownaudio.Audio.Diagnostics.AudioDeviceException;

namespace Ownaudio.Audio.Devices;

/// <summary>
/// Provides access to the system's audio input and output devices.
/// </summary>
/// <remarks>
/// <para>
/// Obtain an instance through <see cref="AudioEngine.Devices"/>.
/// The device list is populated on first access and updated by calling <see cref="Refresh"/>.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Refresh"/> and all property accesses must not be called
/// concurrently on the same instance.
/// </para>
/// <para>
/// <b>Hot-plug support:</b> The <see cref="DeviceListChanged"/> event is currently not
/// implemented.  Poll by calling <see cref="Refresh"/> periodically, or check
/// <see cref="SupportsHotPlug"/> at runtime.
/// </para>
/// </remarks>
public sealed class AudioDeviceManager
{
    #region Fields

    private readonly Safe.AudioEngine _safeEngine;
    private IReadOnlyList<AudioDevice> _playbackDevices = Array.Empty<AudioDevice>();
    private IReadOnlyList<AudioDevice> _captureDevices  = Array.Empty<AudioDevice>();
    private bool _loaded;

    #endregion

    #region Construction

    internal AudioDeviceManager(Safe.AudioEngine safeEngine)
    {
        _safeEngine = safeEngine;
    }

    #endregion

    #region Properties

    /// <summary>
    /// All available output (playback) devices.
    /// Populated on first access or after <see cref="Refresh"/> is called.
    /// </summary>
    public IReadOnlyList<AudioDevice> PlaybackDevices
    {
        get
        {
            EnsureLoaded();
            return _playbackDevices;
        }
    }

    /// <summary>
    /// All available input (capture) devices.
    /// Populated on first access or after <see cref="Refresh"/> is called.
    /// </summary>
    public IReadOnlyList<AudioDevice> CaptureDevices
    {
        get
        {
            EnsureLoaded();
            return _captureDevices;
        }
    }

    /// <summary>
    /// The system-default output device, or <see langword="null"/> if no output device exists.
    /// </summary>
    public AudioDevice? DefaultPlaybackDevice
    {
        get
        {
            EnsureLoaded();

            foreach (AudioDevice d in _playbackDevices)
            {
                if (d.IsDefault)
                {
                    return d;
                }
            }

            return _playbackDevices.Count > 0 ? _playbackDevices[0] : null;
        }
    }

    /// <summary>
    /// The system-default input device, or <see langword="null"/> if no input device exists.
    /// </summary>
    public AudioDevice? DefaultCaptureDevice
    {
        get
        {
            EnsureLoaded();

            foreach (AudioDevice d in _captureDevices)
            {
                if (d.IsDefault)
                {
                    return d;
                }
            }

            return _captureDevices.Count > 0 ? _captureDevices[0] : null;
        }
    }

    /// <summary>
    /// <see langword="false"/> on all current platforms — hot-plug notification via
    /// <see cref="DeviceListChanged"/> is not yet implemented.  Poll with <see cref="Refresh"/>
    /// to detect device changes.
    /// </summary>
    public bool SupportsHotPlug => false;

    #endregion

    #region Events

    /// <summary>
    /// Raised when the list of available devices changes.
    /// </summary>
    /// <remarks>
    /// Not supported on the current platform.  <see cref="SupportsHotPlug"/> is
    /// <see langword="false"/>; this event is never fired.  A future release may implement
    /// hot-plug notification from the native layer.
    /// </remarks>
    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceListChanged;

    #endregion

    #region Public methods

    /// <summary>
    /// Re-enumerates all audio devices from the OS audio subsystem.
    /// </summary>
    /// <remarks>
    /// This is a fast, synchronous operation.  Device names and capabilities may change
    /// between calls if hardware is connected or disconnected.
    /// </remarks>
    /// <exception cref="AudioDeviceException">
    /// Thrown when the native device enumeration call fails.
    /// </exception>
    public void Refresh()
    {
        IReadOnlyList<AudioDevice> oldPlayback = _playbackDevices;
        IReadOnlyList<AudioDevice> oldCapture  = _captureDevices;

        LoadDevices();

        if (DeviceListChanged is not null)
        {
            IReadOnlyList<AudioDevice> added   = ComputeDiff(_playbackDevices, oldPlayback,
                                                              _captureDevices,  oldCapture,
                                                              added: true);
            IReadOnlyList<AudioDevice> removed = ComputeDiff(oldPlayback, _playbackDevices,
                                                              oldCapture,  _captureDevices,
                                                              added: false);

            if (added.Count > 0 || removed.Count > 0)
            {
                DeviceListChanged.Invoke(this, new AudioDeviceChangedEventArgs(added, removed));
            }
        }
    }

    #endregion

    #region Private helpers

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            LoadDevices();
        }
    }

    private void LoadDevices()
    {
        try
        {
            IReadOnlyList<Safe.AudioDevice> safeOutput = _safeEngine.EnumerateOutputDevices();
            IReadOnlyList<Safe.AudioDevice> safeInput  = _safeEngine.EnumerateInputDevices();

            _playbackDevices = MapDevices(safeOutput, AudioDeviceType.Playback);
            _captureDevices  = MapDevices(safeInput,  AudioDeviceType.Capture);
            _loaded = true;
        }
        catch (DeviceException ex)
        {
            throw new AudioDeviceException((int)ex.ErrorCode, ex.Message, ex);
        }
    }

    private static IReadOnlyList<AudioDevice> MapDevices(
        IReadOnlyList<Safe.AudioDevice> source,
        AudioDeviceType type)
    {
        var result = new AudioDevice[source.Count];

        for (int i = 0; i < source.Count; i++)
        {
            Safe.AudioDevice s = source[i];

            bool isDefault = type == AudioDeviceType.Playback
                ? s.IsDefaultOutput
                : s.IsDefaultInput;

            result[i] = new AudioDevice
            {
                Id                = s.Name,
                Name              = s.Name,
                Type              = type,
                IsDefault         = isDefault,
                MaxInputChannels  = s.MaxInputChannels,
                MaxOutputChannels = s.MaxOutputChannels,
                DefaultSampleRate = s.DefaultSampleRate,
            };
        }

        return result;
    }

    private static IReadOnlyList<AudioDevice> ComputeDiff(
        IReadOnlyList<AudioDevice> newList,
        IReadOnlyList<AudioDevice> oldList,
        IReadOnlyList<AudioDevice> newList2,
        IReadOnlyList<AudioDevice> oldList2,
        bool added)
    {
        var result = new System.Collections.Generic.List<AudioDevice>();

        AddDiff(result, newList,  oldList);
        AddDiff(result, newList2, oldList2);

        return result;

        static void AddDiff(
            System.Collections.Generic.List<AudioDevice> acc,
            IReadOnlyList<AudioDevice> @new,
            IReadOnlyList<AudioDevice> old)
        {
            foreach (AudioDevice d in @new)
            {
                bool found = false;
                foreach (AudioDevice o in old)
                {
                    if (o.Id == d.Id) { found = true; break; }
                }

                if (!found)
                {
                    acc.Add(d);
                }
            }
        }
    }

    #endregion
}
