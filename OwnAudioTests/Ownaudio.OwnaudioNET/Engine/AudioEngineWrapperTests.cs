using Ownaudio.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Exceptions;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;
using AudioEngineWrapper = OwnaudioNET.Engine.AudioEngineWrapper;

namespace Ownaudio.OwnaudioNET.Tests.Engine;

/// <summary>
/// Tests for the AudioEngineWrapper class.
/// </summary>
public class AudioEngineWrapperTests : IDisposable
{
    private readonly AudioConfig _testConfig;
    private AudioEngineWrapper? _wrapper;

    public AudioEngineWrapperTests()
    {
        _testConfig = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };
    }

    public void Dispose()
    {
        _wrapper?.Dispose();
    }

    [Fact]
    public void Constructor_WithValidEngine_ShouldInitialize()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);

        // Act
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Assert
        _wrapper.Should().NotBeNull();
        _wrapper.IsRunning.Should().BeFalse();
        _wrapper.FramesPerBuffer.Should().BeGreaterThan(0);
        _wrapper.Config.Should().Be(_testConfig);
    }

    [Fact]
    public void Constructor_WithNullEngine_ShouldThrowArgumentNullException()
    {
        // Arrange & Act
        Action act = () => new AudioEngineWrapper(null!, _testConfig);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullConfig_ShouldThrowArgumentNullException()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);

        // Act
        Action act = () => new AudioEngineWrapper(engine, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Start_ShouldSetIsRunningToTrue()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        _wrapper.Start();

        // Assert
        _wrapper.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Start_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        _wrapper.Start();
        Action act = () => _wrapper.Start();

        // Assert
        act.Should().NotThrow();
        _wrapper.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Stop_AfterStart_ShouldSetIsRunningToFalse()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Start();

        // Act
        _wrapper.Stop();

        // Assert
        _wrapper.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Stop_WithoutStart_ShouldBeIdempotent()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        Action act = () => _wrapper.Stop();

        // Assert
        act.Should().NotThrow();
        _wrapper.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Send_WithValidSamples_ShouldNotThrow()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Start();
        var samples = new float[1024];

        // Act
        Action act = () => _wrapper.Send(samples);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Send_WithoutStart_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        var samples = new float[1024];

        // Act
        Action act = () => _wrapper.Send(samples);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not running*");
    }

    [Fact]
    public void Send_WithEmptySpan_ShouldNotThrow()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Start();

        // Act
        Action act = () => _wrapper.Send(ReadOnlySpan<float>.Empty);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Send_LargeAmount_ShouldTriggerUnderrunEvent()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Start();

        bool underrunTriggered = false;
        _wrapper.BufferUnderrun += (s, e) => underrunTriggered = true;

        // Act - Send way more than buffer can hold
        var largeSamples = new float[1024 * 1024]; // 1MB of samples
        _wrapper.Send(largeSamples);

        // Assert
        underrunTriggered.Should().BeTrue();
        _wrapper.TotalUnderruns.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Receive_AfterStart_ShouldReturnNullOrData()
    {
        // Arrange
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512,
            EnableInput = true
        };
        var engine = AudioEngineFactory.CreateMockEngine(config);
        _wrapper = new AudioEngineWrapper(engine, config);
        _wrapper.Start();

        // Act
        var buffer = _wrapper.Receive(out int count);

        // Assert - Can be null if no input data available
        if (buffer != null)
        {
            count.Should().BeGreaterThan(0);
            buffer.Length.Should().BeGreaterOrEqualTo(count);
        }
        else
        {
            count.Should().Be(0);
        }
    }

    [Fact]
    public void Receive_WithoutStart_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        Action act = () => _wrapper.Receive(out _);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not running*");
    }

    [Fact]
    public void ReturnInputBuffer_WithValidBuffer_ShouldNotThrow()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        var buffer = new float[_wrapper.FramesPerBuffer * _testConfig.Channels];

        // Act
        Action act = () => _wrapper.ReturnInputBuffer(buffer);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ReturnInputBuffer_WithInvalidBuffer_ShouldNotThrow()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        var invalidBuffer = new float[10]; // Wrong size

        // Act
        Action act = () => _wrapper.ReturnInputBuffer(invalidBuffer);

        // Assert
        act.Should().NotThrow(); // Should handle gracefully
    }

    [Fact]
    public void GetOutputDevices_ShouldReturnList()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        var devices = _wrapper.GetOutputDevices();

        // Assert
        devices.Should().NotBeNull();
    }

    [Fact]
    public void GetInputDevices_ShouldReturnList()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        var devices = _wrapper.GetInputDevices();

        // Assert
        devices.Should().NotBeNull();
    }

    [Fact]
    public void ClearOutputBuffer_ShouldNotThrow()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        Action act = () => _wrapper.ClearOutputBuffer();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void OutputBufferAvailable_ShouldReturnNonNegativeValue()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        var available = _wrapper.OutputBufferAvailable;

        // Assert
        available.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void TotalUnderruns_InitialValue_ShouldBeZero()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        var underruns = _wrapper.TotalUnderruns;

        // Assert
        underruns.Should().Be(0);
    }

    [Fact]
    public void TotalPumpedFrames_InitialValue_ShouldBeZero()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        var pumped = _wrapper.TotalPumpedFrames;

        // Assert
        pumped.Should().Be(0);
    }

    [Fact]
    public void TotalPumpedFrames_AfterSendingAudio_ShouldIncrease()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Start();

        var samples = new float[1024];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);

        // Act
        _wrapper.Send(samples);
        Thread.Sleep(100); // Give pump thread time to process

        var pumped = _wrapper.TotalPumpedFrames;

        // Assert
        pumped.Should().BeGreaterThan(0);
    }

    [Fact]
    public void UnderlyingEngine_ShouldReturnEngine()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        var underlying = _wrapper.UnderlyingEngine;

        // Assert
        underlying.Should().NotBeNull();
        underlying.Should().BeSameAs(engine);
    }

    [Fact]
    public void ToString_ShouldReturnMeaningfulString()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        var str = _wrapper.ToString();

        // Assert
        str.Should().NotBeNullOrEmpty();
        str.Should().Contain("48000");
        str.Should().Contain("2ch");
    }

    [Fact]
    public void Dispose_ShouldStopEngineAndCleanup()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Start();

        // Act
        _wrapper.Dispose();

        // Assert
        _wrapper.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Dispose_CalledTwice_ShouldNotThrow()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);

        // Act
        _wrapper.Dispose();
        Action act = () => _wrapper.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Operations_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Dispose();

        // Act & Assert
        Action startAct = () => _wrapper.Start();
        startAct.Should().Throw<ObjectDisposedException>();

        Action stopAct = () => _wrapper.Stop();
        stopAct.Should().Throw<ObjectDisposedException>();

        Action sendAct = () => _wrapper.Send(new float[100]);
        sendAct.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void ConcurrentSend_FromMultipleThreads_ShouldNotCrash()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Start();

        var tasks = new List<Task>();
        var samples = new float[512];

        // Act - Send from multiple threads concurrently
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    _wrapper.Send(samples);
                }
            }));
        }

        // Assert - Should complete without exceptions
        Action act = () => Task.WaitAll(tasks.ToArray());
        act.Should().NotThrow();
    }

    [Fact]
    public void BufferUnderrun_Event_ShouldProvideCorrectEventArgs()
    {
        // Arrange
        var engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _wrapper = new AudioEngineWrapper(engine, _testConfig);
        _wrapper.Start();

        BufferUnderrunEventArgs? receivedArgs = null;
        _wrapper.BufferUnderrun += (s, e) => receivedArgs = e;

        // Act - Overflow the buffer
        var hugeSamples = new float[10 * 1024 * 1024];
        _wrapper.Send(hugeSamples);

        // Assert
        receivedArgs.Should().NotBeNull();
        receivedArgs!.MissedFrames.Should().BeGreaterThan(0);
    }
}
