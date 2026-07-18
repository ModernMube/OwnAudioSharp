using System;
using System.Collections.Generic;
using Ownaudio.Core.Common;

namespace Ownaudio.Core
{
    /// <summary>
    /// The cross-platform engine contract. Implementations have to be RT safe — no allocations
    /// anywhere near the audio thread.
    /// </summary>
    public interface IAudioEngine : IDisposable
    {
        /// <summary>
        /// Raw platform stream handle: IAudioClient on Windows, AudioQueue/AudioUnit on Apple,
        /// snd_pcm_t on Linux, AAudioStream on Android.
        /// </summary>
        IntPtr GetStream();

        /// <summary>
        /// What the device actually gave us, which may not be what AudioConfig asked for.
        /// </summary>
        int FramesPerBuffer { get; }

        /// <summary>
        /// Idle / Running / DeviceDisconnected / Error.
        /// </summary>
        EngineStatus Status { get; }

        /// <summary>
        /// 1 = active, 0 = idle, negative = error.
        /// </summary>
        int OwnAudioEngineActivate();

        /// <summary>
        /// 1 = stopped, 0 = running, negative = error.
        /// </summary>
        int OwnAudioEngineStopped();

        /// <summary>
        /// Has to run before Start(). Blocks — never call it straight from a UI thread,
        /// use InitializeAsync(). Returns 0 on success, negative error code otherwise.
        /// </summary>
        int Initialize(AudioConfig config);

        /// <summary>
        /// Kicks the engine off. Thread safe and idempotent.
        /// </summary>
        int Start();

        /// <summary>
        /// Winds it down. Thread safe and idempotent.
        /// </summary>
        int Stop();

        /// <summary>
        /// Pushes interleaved Float32 samples to the device. Zero-alloc, but it blocks
        /// until the device buffer has room — figure 10-50ms depending on buffer and platform.
        /// </summary>
        /// <exception cref="AudioException">Thrown when device write fails.</exception>
        void Send(Span<float> samples);

        /// <summary>
        /// Pulls captured samples into the caller's own buffer.
        /// </summary>
        /// <returns>Samples written, or a negative error code.</returns>
        int Receives(Span<float> destination);

        /// <summary>
        /// Every output device we can see.
        /// </summary>
        List<AudioDeviceInfo> GetOutputDevices();

        /// <summary>
        /// Every input device we can see.
        /// </summary>
        List<AudioDeviceInfo> GetInputDevices();

        /// <summary>
        /// Switch output by friendly name. Engine has to be stopped first.
        /// </summary>
        int SetOutputDeviceByName(string deviceName);

        /// <summary>
        /// Switch output by zero-based index into the output device list. Stop first.
        /// </summary>
        int SetOutputDeviceByIndex(int deviceIndex);

        /// <summary>
        /// Switch input by friendly name. Stop first.
        /// </summary>
        int SetInputDeviceByName(string deviceName);

        /// <summary>
        /// Switch input by zero-based index into the input device list. Stop first.
        /// </summary>
        int SetInputDeviceByIndex(int deviceIndex);

        /// <summary>
        /// Default output device changed under us.
        /// </summary>
        event EventHandler<AudioDeviceChangedEventArgs> OutputDeviceChanged;

        /// <summary>
        /// Default input device changed under us.
        /// </summary>
        event EventHandler<AudioDeviceChangedEventArgs> InputDeviceChanged;

        /// <summary>
        /// Some device was added, removed, enabled or disabled.
        /// </summary>
        event EventHandler<AudioDeviceStateChangedEventArgs> DeviceStateChanged;

        /// <summary>
        /// A dropped device came back and we picked playback/recording up again.
        /// </summary>
        event EventHandler<AudioDeviceReconnectedEventArgs> DeviceReconnected;

        /// <summary>
        /// Freezes the device watcher task. Worth doing while a VST editor window opens,
        /// enumeration likes to fight with the UI thread.
        /// </summary>
        void PauseDeviceMonitoring();

        /// <summary>
        /// Lets the watcher run again.
        /// </summary>
        void ResumeDeviceMonitoring();
    }
}
