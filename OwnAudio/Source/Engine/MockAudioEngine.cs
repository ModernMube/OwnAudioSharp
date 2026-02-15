using System;
using System.Collections.Generic;
using System.Threading;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Engine;

/// <summary>
/// Mock implementation of IAudioEngine for testing without audio hardware.
/// Simulates audio I/O timing and provides test hooks for validation.
/// </summary>
/// <remarks>
/// This mock engine is useful for:
/// - Unit testing audio processing logic
/// - Developing on platforms without audio hardware
/// - Testing on unsupported platforms
/// - Continuous integration environments
///
/// The mock engine simulates realistic timing based on buffer size and sample rate,
/// and can optionally generate a 440Hz sine wave for testing.
/// </remarks>
public sealed class MockAudioEngine : IAudioEngine
{
    private readonly object _lock = new object();
    private readonly bool _generateTestSignal;
    private AudioConfig? _config;
    private Timer? _simulationTimer;
    private volatile int _state; // 0 = stopped, 1 = running, -1 = error
    private volatile int _sendCallCount;
    private long _totalSamplesSent; // Use Interlocked.Read for thread-safe access
    private volatile int _receiveCallCount;
    private long _totalSamplesReceived; // Use Interlocked.Read for thread-safe access
    private bool _disposed;
    private double _sinePhase; // Phase accumulator for sine wave generation

    /// <summary>
    /// Gets the number of times Send() has been called.
    /// </summary>
    public int SendCallCount => _sendCallCount;

    /// <summary>
    /// Gets the total number of samples sent through Send().
    /// </summary>
    public long TotalSamplesSent => Interlocked.Read(ref _totalSamplesSent);

    /// <summary>
    /// Gets the number of times Receives() has been called.
    /// </summary>
    public int ReceiveCallCount => _receiveCallCount;

    /// <summary>
    /// Gets the total number of samples received through Receives().
    /// </summary>
    public long TotalSamplesReceived => Interlocked.Read(ref _totalSamplesReceived);

    /// <summary>
    /// Event raised periodically to simulate audio callback timing.
    /// Useful for testing components that need to respond to audio timing events.
    /// </summary>
    public event EventHandler? SimulatedCallback;

    /// <inheritdoc/>
    public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;

    /// <summary>
    /// Initializes a new instance of the MockAudioEngine class.
    /// </summary>
    /// <param name="generateTestSignal">If true, generates a 440Hz sine wave in Send() calls for testing.</param>
    public MockAudioEngine(bool generateTestSignal = false)
    {
        _generateTestSignal = generateTestSignal;
        _sinePhase = 0.0;
    }

    /// <inheritdoc/>
    public IntPtr GetStream()
    {
        // Mock engine has no native stream
        return IntPtr.Zero;
    }

    /// <inheritdoc/>
    public int FramesPerBuffer
    {
        get
        {
            lock (_lock)
            {
                return _config?.BufferSize ?? 0;
            }
        }
    }

    /// <inheritdoc/>
    public int OwnAudioEngineActivate()
    {
        // Returns activation state: 1 = active, 0 = idle, <0 = error
        int currentState = _state;
        if (currentState < 0)
            return -1; // Error state

        return currentState == 1 ? 1 : 0;
    }

    /// <inheritdoc/>
    public int OwnAudioEngineStopped()
    {
        // Returns stopped state: 1 = stopped, 0 = running, <0 = error
        int currentState = _state;
        if (currentState < 0)
            return -1; // Error state

        return currentState == 0 ? 1 : 0;
    }

    /// <inheritdoc/>
    public int Initialize(AudioConfig config)
    {
        if (config == null)
            return -1;

        if (!config.Validate())
            return -2;

        lock (_lock)
        {
            if (_disposed)
                return -3;

            if (_config != null)
                return -4; // Already initialized

            _config = config;
            _state = 0; // Stopped/idle state
            _sendCallCount = 0;
            _totalSamplesSent = 0;
            _receiveCallCount = 0;
            _totalSamplesReceived = 0;
            _sinePhase = 0.0;

            return 0; // Success
        }
    }

    /// <inheritdoc/>
    public int Start()
    {
        lock (_lock)
        {
            if (_disposed)
                return -1;

            if (_config == null)
                return -2; // Not initialized

            if (_state == 1)
                return 0; // Already running (idempotent)

            // Calculate callback interval based on buffer size and sample rate
            // Interval = (BufferSize / SampleRate) * 1000ms
            double intervalMs = (_config.BufferSize / (double)_config.SampleRate) * 1000.0;

            // Start simulation timer
            _simulationTimer = new Timer(
                SimulationCallback,
                null,
                TimeSpan.FromMilliseconds(intervalMs),
                TimeSpan.FromMilliseconds(intervalMs));

            _state = 1; // Running state
            return 0; // Success
        }
    }

    /// <inheritdoc/>
    public int Stop()
    {
        lock (_lock)
        {
            if (_disposed)
                return -1;

            if (_state == 0)
                return 0; // Already stopped (idempotent)

            // Stop simulation timer
            _simulationTimer?.Dispose();
            _simulationTimer = null;

            _state = 0; // Stopped state
            return 0; // Success
        }
    }

    /// <inheritdoc/>
    public void Send(Span<float> samples)
    {
        if (_disposed)
            throw new AudioException("Cannot send samples: engine is disposed.");

        if (_config == null)
            throw new AudioException("Cannot send samples: engine not initialized.");

        if (_state != 1)
            throw new AudioException("Cannot send samples: engine not running.");

        // Track statistics
        Interlocked.Increment(ref _sendCallCount);
        Interlocked.Add(ref _totalSamplesSent, samples.Length);

        // If test signal generation is enabled, write 440Hz sine wave
        if (_generateTestSignal && _config != null)
        {
            GenerateSineWave(samples, _config.SampleRate, _config.Channels);
        }

        // Simulate some processing time (minimal)
        // In real engine, this would be the time to copy to device buffer
        Thread.SpinWait(100);
    }

    /// <inheritdoc/>
    public int Receives(out float[] samples)
    {
        if (_disposed)
        {
            samples = Array.Empty<float>();
            return -1;
        }

        if (_config == null)
        {
            samples = Array.Empty<float>();
            return -2; // Not initialized
        }

        if (_state != 1)
        {
            samples = Array.Empty<float>();
            return -3; // Not running
        }

        if (!_config.EnableInput)
        {
            samples = Array.Empty<float>();
            return -4; // Input not enabled
        }

        // Track statistics
        Interlocked.Increment(ref _receiveCallCount);

        // Generate mock input data (silence or test signal)
        int sampleCount = _config.BufferSize * _config.Channels;
        samples = new float[sampleCount];

        if (_generateTestSignal)
        {
            GenerateSineWave(samples.AsSpan(), _config.SampleRate, _config.Channels);
        }
        // else: samples already initialized to zeros (silence)

        Interlocked.Add(ref _totalSamplesReceived, sampleCount);

        return 0; // Success
    }

    /// <inheritdoc/>
    public List<AudioDeviceInfo> GetOutputDevices()
    {
        // Return mock output devices
        return new List<AudioDeviceInfo>
        {
            new AudioDeviceInfo(
                deviceId: "mock-output-default",
                name: "Mock Output Device (Default)",
                engineName: "Mock",
                isInput: false,
                isOutput: true,
                isDefault: true,
                state: AudioDeviceState.Active),
            new AudioDeviceInfo(
                deviceId: "mock-output-secondary",
                name: "Mock Output Device (Secondary)",
                engineName: "Mock",
                isInput: false,
                isOutput: true,
                isDefault: false,
                state: AudioDeviceState.Active)
        };
    }

    /// <inheritdoc/>
    public List<AudioDeviceInfo> GetInputDevices()
    {
        // Return mock input devices
        return new List<AudioDeviceInfo>
        {
            new AudioDeviceInfo(
                deviceId: "mock-input-default",
                name: "Mock Input Device (Default)",
                engineName: "Mock",
                isInput: true,
                isOutput: false,
                isDefault: true,
                state: AudioDeviceState.Active),
            new AudioDeviceInfo(
                deviceId: "mock-input-secondary",
                name: "Mock Input Device (Secondary)",
                engineName: "Mock",
                isInput: true,
                isOutput: false,
                isDefault: false,
                state: AudioDeviceState.Active)
        };
    }

    /// <inheritdoc/>
    public int SetOutputDeviceByName(string deviceName)
    {
        if (_disposed)
            return -1;

        if (_state == 1)
            return -2; // Cannot change device while running

        if (string.IsNullOrWhiteSpace(deviceName))
            return -3; // Invalid device name

        // Mock implementation: accept any device name
        return 0; // Success
    }

    /// <inheritdoc/>
    public int SetOutputDeviceByIndex(int deviceIndex)
    {
        if (_disposed)
            return -1;

        if (_state == 1)
            return -2; // Cannot change device while running

        if (deviceIndex < 0)
            return -3; // Invalid device index

        // Mock implementation: accept any valid index
        return 0; // Success
    }

    /// <inheritdoc/>
    public int SetInputDeviceByName(string deviceName)
    {
        if (_disposed)
            return -1;

        if (_state == 1)
            return -2; // Cannot change device while running

        if (string.IsNullOrWhiteSpace(deviceName))
            return -3; // Invalid device name

        // Mock implementation: accept any device name
        return 0; // Success
    }

    /// <inheritdoc/>
    public int SetInputDeviceByIndex(int deviceIndex)
    {
        if (_disposed)
            return -1;

        if (_state == 1)
            return -2; // Cannot change device while running

        if (deviceIndex < 0)
            return -3; // Invalid device index

        // Mock implementation: accept any valid index
        return 0; // Success
    }

    /// <inheritdoc/>
    public void PauseDeviceMonitoring()
    {
        // Mock implementation: no device monitoring to pause
    }

    /// <inheritdoc/>
    public void ResumeDeviceMonitoring()
    {
        // Mock implementation: no device monitoring to resume
    }

    /// <summary>
    /// Resets all statistics counters to zero.
    /// Useful for testing scenarios that need clean state.
    /// </summary>
    public void ResetStatistics()
    {
        lock (_lock)
        {
            _sendCallCount = 0;
            _totalSamplesSent = 0;
            _receiveCallCount = 0;
            _totalSamplesReceived = 0;
        }
    }

    /// <summary>
    /// Simulates an output device change event for testing.
    /// </summary>
    /// <param name="oldDeviceId">The ID of the old device.</param>
    /// <param name="newDeviceId">The ID of the new device.</param>
    public void SimulateOutputDeviceChange(string oldDeviceId, string newDeviceId)
    {
        var newDevice = new AudioDeviceInfo(
            newDeviceId,
            $"Mock Output Device ({newDeviceId})",
            "Mock",
            isInput: false,
            isOutput: true,
            isDefault: true,
            AudioDeviceState.Active);

        OutputDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(oldDeviceId, newDeviceId, newDevice));
    }

    /// <summary>
    /// Simulates an input device change event for testing.
    /// </summary>
    /// <param name="oldDeviceId">The ID of the old device.</param>
    /// <param name="newDeviceId">The ID of the new device.</param>
    public void SimulateInputDeviceChange(string oldDeviceId, string newDeviceId)
    {
        var newDevice = new AudioDeviceInfo(
            newDeviceId,
            $"Mock Input Device ({newDeviceId})",
            "Mock",
            isInput: true,
            isOutput: false,
            isDefault: true,
            AudioDeviceState.Active);

        InputDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(oldDeviceId, newDeviceId, newDevice));
    }

    /// <summary>
    /// Simulates a device state change event for testing.
    /// </summary>
    /// <param name="deviceId">The ID of the device.</param>
    /// <param name="newState">The new state of the device.</param>
    public void SimulateDeviceStateChange(string deviceId, AudioDeviceState newState)
    {
        var device = new AudioDeviceInfo(
            deviceId,
            $"Mock Device ({deviceId})",
            "Mock",
            isInput: true,
            isOutput: true,
            isDefault: true,
            newState);

        DeviceStateChanged?.Invoke(this, new AudioDeviceStateChangedEventArgs(deviceId, newState, device));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            Stop();
            _config = null;
            _state = -1; // Error state
            _disposed = true;
        }
    }

    /// <summary>
    /// Timer callback that simulates periodic audio processing timing.
    /// </summary>
    private void SimulationCallback(object? state)
    {
        SimulatedCallback?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Generates a 440Hz sine wave in the provided buffer.
    /// </summary>
    /// <param name="buffer">The buffer to fill with sine wave samples.</param>
    /// <param name="sampleRate">The sample rate in Hz.</param>
    /// <param name="channels">The number of channels.</param>
    private void GenerateSineWave(Span<float> buffer, int sampleRate, int channels)
    {
        const double frequency = 440.0; // A4 note
        const double amplitude = 0.25; // 25% volume to avoid clipping
        double phaseIncrement = 2.0 * Math.PI * frequency / sampleRate;

        int frameCount = buffer.Length / channels;

        for (int frame = 0; frame < frameCount; frame++)
        {
            float sample = (float)(amplitude * Math.Sin(_sinePhase));

            // Write same sample to all channels
            for (int ch = 0; ch < channels; ch++)
            {
                buffer[frame * channels + ch] = sample;
            }

            _sinePhase += phaseIncrement;

            // Keep phase in [0, 2π] to prevent overflow
            if (_sinePhase >= 2.0 * Math.PI)
                _sinePhase -= 2.0 * Math.PI;
        }
    }
}
