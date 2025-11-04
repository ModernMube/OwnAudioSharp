using System;
using Ownaudio.Core.Common;

namespace Ownaudio.Core
{
    /// <summary>
    /// Cross-platform audio engine interface with zero-allocation guarantees.
    /// All implementations must be real-time safe and GC-optimized.
    /// </summary>
    public interface IAudioEngine : IDisposable
    {
        /// <summary>
        /// Gets the native audio stream handle (platform-specific).
        /// Windows: IAudioClient pointer
        /// macOS/iOS: AudioQueue/AudioUnit pointer
        /// Linux: snd_pcm_t pointer
        /// Android: AAudioStream pointer
        /// </summary>
        IntPtr GetStream();

        /// <summary>
        /// Gets the actual frames per buffer size negotiated with the audio device.
        /// May differ from the requested buffer size in AudioConfig.
        /// </summary>
        int FramesPerBuffer { get; }

        /// <summary>
        /// Gets the activation state of the audio engine.
        /// </summary>
        /// <returns>
        /// 1 = playing/recording (active)
        /// 0 = idle (initialized but not running)
        /// &lt;0 = error state
        /// </returns>
        int OwnAudioEngineActivate();

        /// <summary>
        /// Gets the stopped state of the audio engine.
        /// </summary>
        /// <returns>
        /// 1 = stopped
        /// 0 = running
        /// &lt;0 = error state
        /// </returns>
        int OwnAudioEngineStopped();

        /// <summary>
        /// Initializes the audio engine with the specified configuration.
        /// Must be called before Start().
        /// </summary>
        /// <param name="config">Audio configuration parameters.</param>
        /// <returns>0 on success, negative error code on failure.</returns>
        int Initialize(AudioConfig config);

        /// <summary>
        /// Starts the audio engine. This method is thread-safe and idempotent.
        /// </summary>
        /// <returns>0 on success, negative error code on failure.</returns>
        int Start();

        /// <summary>
        /// Stops the audio engine gracefully. This method is thread-safe and idempotent.
        /// </summary>
        /// <returns>0 on success, negative error code on failure.</returns>
        int Stop();

        /// <summary>
        /// Sends audio samples to the output device in a blocking manner.
        /// This method is zero-allocation and real-time safe.
        /// </summary>
        /// <param name="samples">Audio samples in Float32 format, interleaved.</param>
        /// <exception cref="AudioException">Thrown when device write fails.</exception>
        /// <remarks>
        /// This call blocks until the device buffer has space available.
        /// Expected latency: 10-50ms depending on buffer size and platform.
        /// </remarks>
        void Send(Span<float> samples);

        /// <summary>
        /// Receives audio samples from the input device.
        /// Uses a pre-allocated buffer pool to minimize allocations.
        /// </summary>
        /// <param name="samples">Output array containing captured audio samples.</param>
        /// <returns>0 on success, negative error code on failure.</returns>
        /// <remarks>
        /// The output array comes from an object pool. Return it when done if applicable.
        /// </remarks>
        int Receives(out float[] samples);

        /// <summary>
        /// Gets a list of all available output devices.
        /// </summary>
        /// <returns>List of output device information.</returns>
        System.Collections.Generic.List<AudioDeviceInfo> GetOutputDevices();

        /// <summary>
        /// Gets a list of all available input devices.
        /// </summary>
        /// <returns>List of input device information.</returns>
        System.Collections.Generic.List<AudioDeviceInfo> GetInputDevices();

        /// <summary>
        /// Changes the output device by device name.
        /// The engine must be stopped before changing devices.
        /// </summary>
        /// <param name="deviceName">The friendly name of the device.</param>
        /// <returns>0 on success, negative error code on failure.</returns>
        int SetOutputDeviceByName(string deviceName);

        /// <summary>
        /// Changes the output device by index in the device list.
        /// The engine must be stopped before changing devices.
        /// </summary>
        /// <param name="deviceIndex">The zero-based index of the device in the output device list.</param>
        /// <returns>0 on success, negative error code on failure.</returns>
        int SetOutputDeviceByIndex(int deviceIndex);

        /// <summary>
        /// Changes the input device by device name.
        /// The engine must be stopped before changing devices.
        /// </summary>
        /// <param name="deviceName">The friendly name of the device.</param>
        /// <returns>0 on success, negative error code on failure.</returns>
        int SetInputDeviceByName(string deviceName);

        /// <summary>
        /// Changes the input device by index in the device list.
        /// The engine must be stopped before changing devices.
        /// </summary>
        /// <param name="deviceIndex">The zero-based index of the device in the input device list.</param>
        /// <returns>0 on success, negative error code on failure.</returns>
        int SetInputDeviceByIndex(int deviceIndex);

        /// <summary>
        /// Event raised when the default output device changes.
        /// </summary>
        event EventHandler<AudioDeviceChangedEventArgs> OutputDeviceChanged;

        /// <summary>
        /// Event raised when the default input device changes.
        /// </summary>
        event EventHandler<AudioDeviceChangedEventArgs> InputDeviceChanged;

        /// <summary>
        /// Event raised when a device state changes (added, removed, enabled, disabled).
        /// </summary>
        event EventHandler<AudioDeviceStateChangedEventArgs> DeviceStateChanged;
    }
}
