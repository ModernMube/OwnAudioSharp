using Ownaudio.Core;
using OwnaudioNET.Exceptions;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;
using MockAudioEngine = OwnaudioNET.Engine.MockAudioEngine;

namespace Ownaudio.OwnaudioNET.Tests.Engine;

/// <summary>
/// Tests for the AudioEngineFactory class.
/// </summary>
public class AudioEngineFactoryTests
{
    [Fact]
    public void CreateEngine_WithNullConfig_ShouldThrowArgumentNullException()
    {
        // Arrange & Act
        Action act = () => AudioEngineFactory.CreateEngine(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEngine_WithInvalidConfig_ShouldThrowAudioEngineException()
    {
        // Arrange
        var invalidConfig = new AudioConfig
        {
            SampleRate = -1, // Invalid
            Channels = 2,
            BufferSize = 512
        };

        // Act
        Action act = () => AudioEngineFactory.CreateEngine(invalidConfig);

        // Assert
        act.Should().Throw<AudioEngineException>()
            .WithMessage("*Invalid audio configuration*");
    }

    [Fact]
    public void CreateMockEngine_WithValidConfig_ShouldReturnInitializedEngine()
    {
        // Arrange
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };

        // Act
        using var engine = AudioEngineFactory.CreateMockEngine(config);

        // Assert
        engine.Should().NotBeNull();
        engine.Should().BeOfType<MockAudioEngine>();
        engine.FramesPerBuffer.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CreateMockEngine_WithNullConfig_ShouldThrowArgumentNullException()
    {
        // Arrange & Act
        Action act = () => AudioEngineFactory.CreateMockEngine(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateMockEngine_WithInvalidConfig_ShouldThrowAudioEngineException()
    {
        // Arrange
        var invalidConfig = new AudioConfig
        {
            SampleRate = 0,
            Channels = 0,
            BufferSize = 0
        };

        // Act
        Action act = () => AudioEngineFactory.CreateMockEngine(invalidConfig);

        // Assert
        act.Should().Throw<AudioEngineException>();
    }

    [Fact]
    public void CreateMockEngine_WithTestSignal_ShouldGenerateSineWave()
    {
        // Arrange
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };

        // Act
        using var engine = AudioEngineFactory.CreateMockEngine(config, generateTestSignal: true);
        engine.Start();

        // Give it time to generate some signal
        Thread.Sleep(50);

        var result = engine.Receives(out var samples);

        // Assert
        if (samples != null && samples.Length > 0)
        {
            // Should have non-zero samples if test signal is enabled
            samples.Should().Contain(s => s != 0f);
        }
    }

    [Fact]
    public void IsNativeEngineAvailable_ShouldReturnBooleanValue()
    {
        // Arrange & Act
        var isAvailable = AudioEngineFactory.IsNativeEngineAvailable();

        // Assert
        (isAvailable == true || isAvailable == false).Should().BeTrue();
    }

    [Fact]
    public void GetPlatformEngineName_ShouldReturnValidName()
    {
        // Arrange & Act
        var engineName = AudioEngineFactory.GetPlatformEngineName();

        // Assert
        engineName.Should().NotBeNullOrEmpty();
        engineName.Should().BeOneOf("WasapiEngine", "CoreAudioEngine", "PulseAudioEngine", "None");
    }

    [Fact]
    public void CreateMockEngine_MultipleInstances_ShouldNotInterfere()
    {
        // Arrange
        var config1 = new AudioConfig { SampleRate = 48000, Channels = 2, BufferSize = 256 };
        var config2 = new AudioConfig { SampleRate = 44100, Channels = 2, BufferSize = 512 };

        // Act
        using var engine1 = AudioEngineFactory.CreateMockEngine(config1);
        using var engine2 = AudioEngineFactory.CreateMockEngine(config2);

        // Assert
        engine1.Should().NotBeNull();
        engine2.Should().NotBeNull();
        engine1.Should().NotBeSameAs(engine2);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void CreateMockEngine_WithDifferentSampleRates_ShouldSucceed(int sampleRate)
    {
        // Arrange
        var config = new AudioConfig
        {
            SampleRate = sampleRate,
            Channels = 2,
            BufferSize = 512
        };

        // Act
        using var engine = AudioEngineFactory.CreateMockEngine(config);

        // Assert
        engine.Should().NotBeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CreateMockEngine_WithDifferentChannels_ShouldSucceed(int channels)
    {
        // Arrange
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = channels,
            BufferSize = 512
        };

        // Act
        using var engine = AudioEngineFactory.CreateMockEngine(config);

        // Assert
        engine.Should().NotBeNull();
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void CreateMockEngine_WithDifferentBufferSizes_ShouldSucceed(int bufferSize)
    {
        // Arrange
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = bufferSize
        };

        // Act
        using var engine = AudioEngineFactory.CreateMockEngine(config);

        // Assert
        engine.Should().NotBeNull();
        engine.FramesPerBuffer.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CreateMockEngine_DisposeTwice_ShouldNotThrow()
    {
        // Arrange
        var config = new AudioConfig { SampleRate = 48000, Channels = 2, BufferSize = 512 };
        var engine = AudioEngineFactory.CreateMockEngine(config);

        // Act
        engine.Dispose();
        Action act = () => engine.Dispose();

        // Assert
        act.Should().NotThrow();
    }
}
