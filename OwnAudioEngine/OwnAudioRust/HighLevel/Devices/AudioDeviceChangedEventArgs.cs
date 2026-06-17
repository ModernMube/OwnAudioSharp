using System;
using System.Collections.Generic;

namespace Ownaudio.Audio.Devices;

/// <summary>
/// Provides data for the <see cref="AudioDeviceManager.DeviceListChanged"/> event.
/// </summary>
public sealed class AudioDeviceChangedEventArgs : EventArgs
{
    #region Properties

    /// <summary>Devices that were added since the last enumeration.</summary>
    public IReadOnlyList<AudioDevice> AddedDevices { get; }

    /// <summary>Devices that were removed since the last enumeration.</summary>
    public IReadOnlyList<AudioDevice> RemovedDevices { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="AudioDeviceChangedEventArgs"/>.
    /// </summary>
    /// <param name="added">Newly available devices.</param>
    /// <param name="removed">Devices that are no longer available.</param>
    public AudioDeviceChangedEventArgs(
        IReadOnlyList<AudioDevice> added,
        IReadOnlyList<AudioDevice> removed)
    {
        AddedDevices   = added;
        RemovedDevices = removed;
    }

    #endregion
}
