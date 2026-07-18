using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Core
{
    /// <summary>
    /// Task.Run wrappers around the blocking IAudioEngine calls, so a UI thread
    /// never sits on them.
    /// </summary>
    public static class AudioEngineAsyncExtensions
    {
        /// <summary>
        /// Init off-thread. Returns 0 on success, negative error code otherwise.
        /// </summary>
        public static Task<int> InitializeAsync(this IAudioEngine engine, AudioConfig config, CancellationToken cancellationToken = default)
            => Task.Run(() => engine.Initialize(config), cancellationToken);

        /// <summary>
        /// Stop off-thread.
        /// </summary>
        public static Task<int> StopAsync(this IAudioEngine engine, CancellationToken cancellationToken = default)
            => Task.Run(() => engine.Stop(), cancellationToken);

        /// <summary>
        /// Device enumeration can take a while on some hosts, keep it off the UI thread.
        /// </summary>
        public static Task<List<AudioDeviceInfo>> GetOutputDevicesAsync(this IAudioEngine engine, CancellationToken cancellationToken = default)
            => Task.Run(() => engine.GetOutputDevices(), cancellationToken);

        /// <summary>
        /// Same for the capture side.
        /// </summary>
        public static Task<List<AudioDeviceInfo>> GetInputDevicesAsync(this IAudioEngine engine, CancellationToken cancellationToken = default)
            => Task.Run(() => engine.GetInputDevices(), cancellationToken);

        /// <summary>
        /// Switch output device by its friendly name.
        /// </summary>
        public static Task<int> SetOutputDeviceByNameAsync(this IAudioEngine engine, string deviceName, CancellationToken cancellationToken = default)
            => Task.Run(() => engine.SetOutputDeviceByName(deviceName), cancellationToken);

        /// <summary>
        /// Switch input device by its friendly name.
        /// </summary>
        public static Task<int> SetInputDeviceByNameAsync(this IAudioEngine engine, string deviceName, CancellationToken cancellationToken = default)
            => Task.Run(() => engine.SetInputDeviceByName(deviceName), cancellationToken);
    }
}
