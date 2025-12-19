using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Core
{
    /// <summary>
    /// Async extensions for IAudioEngine to prevent UI thread blocking.
    /// These extension methods wrap blocking IAudioEngine operations in Task.Run
    /// to ensure they don't freeze UI threads in desktop/mobile applications.
    /// </summary>
    public static class AudioEngineAsyncExtensions
    {
        /// <summary>
        /// Initializes the audio engine asynchronously.
        /// </summary>
        /// <param name="engine">The audio engine to initialize.</param>
        /// <param name="config">Audio configuration parameters.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that completes with 0 on success, negative error code on failure.</returns>
        /// <exception cref="ArgumentNullException">Thrown if engine or config is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown if cancellation is requested.</exception>
        public static async Task<int> InitializeAsync(
            this IAudioEngine engine,
            AudioConfig config,
            CancellationToken cancellationToken = default)
        {
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            return await Task.Run(() => engine.Initialize(config), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Stops the audio engine asynchronously.
        /// </summary>
        /// <param name="engine">The audio engine to stop.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that completes with 0 on success, negative error code on failure.</returns>
        /// <exception cref="ArgumentNullException">Thrown if engine is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown if cancellation is requested.</exception>
        public static async Task<int> StopAsync(
            this IAudioEngine engine,
            CancellationToken cancellationToken = default)
        {
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));

            return await Task.Run(() => engine.Stop(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets output devices asynchronously.
        /// </summary>
        /// <param name="engine">The audio engine.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that completes with a list of output device information.</returns>
        /// <exception cref="ArgumentNullException">Thrown if engine is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown if cancellation is requested.</exception>
        public static async Task<List<AudioDeviceInfo>> GetOutputDevicesAsync(
            this IAudioEngine engine,
            CancellationToken cancellationToken = default)
        {
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));

            return await Task.Run(() => engine.GetOutputDevices(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets input devices asynchronously.
        /// </summary>
        /// <param name="engine">The audio engine.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that completes with a list of input device information.</returns>
        /// <exception cref="ArgumentNullException">Thrown if engine is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown if cancellation is requested.</exception>
        public static async Task<List<AudioDeviceInfo>> GetInputDevicesAsync(
            this IAudioEngine engine,
            CancellationToken cancellationToken = default)
        {
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));

            return await Task.Run(() => engine.GetInputDevices(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Changes the output device by name asynchronously.
        /// </summary>
        /// <param name="engine">The audio engine.</param>
        /// <param name="deviceName">The friendly name of the device.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that completes with 0 on success, negative error code on failure.</returns>
        /// <exception cref="ArgumentNullException">Thrown if engine or deviceName is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown if cancellation is requested.</exception>
        public static async Task<int> SetOutputDeviceByNameAsync(
            this IAudioEngine engine,
            string deviceName,
            CancellationToken cancellationToken = default)
        {
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));
            if (deviceName == null)
                throw new ArgumentNullException(nameof(deviceName));

            return await Task.Run(() => engine.SetOutputDeviceByName(deviceName), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Changes the input device by name asynchronously.
        /// </summary>
        /// <param name="engine">The audio engine.</param>
        /// <param name="deviceName">The friendly name of the device.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that completes with 0 on success, negative error code on failure.</returns>
        /// <exception cref="ArgumentNullException">Thrown if engine or deviceName is null.</exception>
        /// <exception cref="OperationCanceledException">Thrown if cancellation is requested.</exception>
        public static async Task<int> SetInputDeviceByNameAsync(
            this IAudioEngine engine,
            string deviceName,
            CancellationToken cancellationToken = default)
        {
            if (engine == null)
                throw new ArgumentNullException(nameof(engine));
            if (deviceName == null)
                throw new ArgumentNullException(nameof(deviceName));

            return await Task.Run(() => engine.SetInputDeviceByName(deviceName), cancellationToken).ConfigureAwait(false);
        }
    }
}
