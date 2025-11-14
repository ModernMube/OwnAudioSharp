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
    /// <remarks>
    /// IMPORTANT: Always use these async methods from UI threads instead of calling
    /// the synchronous IAudioEngine methods directly.
    ///
    /// Example (WPF/MAUI/Avalonia):
    /// <code>
    /// // BAD - blocks UI thread
    /// engine.Initialize(config);
    ///
    /// // GOOD - UI remains responsive
    /// await engine.InitializeAsync(config);
    /// </code>
    /// </remarks>
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
        /// <remarks>
        /// This method wraps the blocking Initialize() call in Task.Run to prevent UI thread blocking.
        /// Expected duration: 50-5000ms depending on platform:
        /// - Windows WASAPI: 50-200ms
        /// - Linux PulseAudio: 100-5000ms (longest!)
        /// - macOS Core Audio: 50-300ms
        /// </remarks>
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
        /// <remarks>
        /// This method wraps the blocking Stop() call in Task.Run to prevent UI thread blocking.
        ///
        /// ⚠️ WARNING: This method waits for the audio thread to finish (up to 2 seconds).
        /// While wrapped in Task.Run, it's still recommended to call this during application
        /// shutdown or when the user expects a brief delay.
        ///
        /// The Stop() method will attempt graceful shutdown with a 2-second timeout.
        /// If the audio thread doesn't stop within that time, it will be forcefully aborted.
        /// </remarks>
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
        /// <remarks>
        /// This method wraps GetOutputDevices() in Task.Run as device enumeration
        /// can take 10-50ms depending on the number of devices and platform.
        ///
        /// While relatively fast, it's still recommended to use async for UI applications
        /// to maintain responsiveness during device enumeration.
        /// </remarks>
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
        /// <remarks>
        /// This method wraps GetInputDevices() in Task.Run as device enumeration
        /// can take 10-50ms depending on the number of devices and platform.
        ///
        /// While relatively fast, it's still recommended to use async for UI applications
        /// to maintain responsiveness during device enumeration.
        /// </remarks>
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
        /// <remarks>
        /// The engine must be stopped before changing devices.
        /// Device switching can take 50-200ms as it requires releasing and re-acquiring audio resources.
        /// </remarks>
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
        /// <remarks>
        /// The engine must be stopped before changing devices.
        /// Device switching can take 50-200ms as it requires releasing and re-acquiring audio resources.
        /// </remarks>
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
