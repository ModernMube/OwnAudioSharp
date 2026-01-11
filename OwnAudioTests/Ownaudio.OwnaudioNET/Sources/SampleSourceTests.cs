using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Sources;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// Tests for the SampleSource class.
/// </summary>
public class SampleSourceTests : IDisposable
{
    private readonly AudioConfig _testConfig;
    private SampleSource? _source;

    public SampleSourceTests()
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
        _source?.Dispose();
    }

    [Fact]
    public void Constructor_WithValidSamples_ShouldInitialize()
    {
        // Arrange
        var samples = new float[4800]; // 0.05 seconds at 48kHz stereo

        // Act
        _source = new SampleSource(samples, _testConfig);

        // Assert
        _source.Should().NotBeNull();
        _source.Duration.Should().BeApproximately(0.05, 0.001);
        _source.Config.Should().Be(_testConfig);
    }

    [Fact]
    public void Constructor_WithNullSamples_ShouldThrowArgumentNullException()
    {
        // Arrange & Act
        Action act = () => new SampleSource(null!, _testConfig);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithEmptySamples_ShouldThrowArgumentNullException()
    {
        // Arrange & Act
        Action act = () => new SampleSource(Array.Empty<float>(), _testConfig);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullConfig_ShouldThrowArgumentNullException()
    {
        // Arrange
        var samples = new float[4800];

        // Act
        Action act = () => new SampleSource(samples, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_DynamicMode_ShouldAllowZeroLengthInitially()
    {
        // Arrange & Act
        _source = new SampleSource(1024, _testConfig);

        // Assert
        _source.Should().NotBeNull();
        _source.AllowDynamicUpdate.Should().BeTrue();
        _source.Duration.Should().Be(0.0);
    }

    [Fact]
    public void Constructor_DynamicMode_WithInvalidBufferSize_ShouldThrow()
    {
        // Arrange & Act
        Action act = () => new SampleSource(0, _testConfig);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReadSamples_WhenPlaying_ShouldReturnAudioData()
    {
        // Arrange
        var samples = new float[4800];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);

        _source = new SampleSource(samples, _testConfig);
        _source.Play();

        var buffer = new float[1024];

        // Act
        int framesRead = _source.ReadSamples(buffer, 512);

        // Assert
        framesRead.Should().BeGreaterThan(0);
        buffer.Should().Contain(s => s != 0f); // Should have audio data
    }

    [Fact]
    public void ReadSamples_WhenNotPlaying_ShouldReturnSilence()
    {
        // Arrange
        var samples = new float[4800];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 1.0f;

        _source = new SampleSource(samples, _testConfig);
        // Don't call Play()

        var buffer = new float[1024];

        // Act
        int framesRead = _source.ReadSamples(buffer, 512);

        // Assert
        buffer.Should().AllBeEquivalentTo(0f); // Should be silence
    }

    [Fact]
    public void ReadSamples_BeyondEndOfData_ShouldReturnSilenceAndSetEndOfStream()
    {
        // Arrange
        var samples = new float[480]; // Very short: 0.005 seconds
        _source = new SampleSource(samples, _testConfig);
        _source.Play();

        var buffer = new float[10000]; // Request more than available

        // Act
        _source.ReadSamples(buffer, 5000);

        // Assert
        _source.IsEndOfStream.Should().BeTrue();
        _source.State.Should().Be(AudioState.EndOfStream);
    }

    [Fact]
    public void ReadSamples_WithLoopEnabled_ShouldRepeatAudio()
    {
        // Arrange
        var samples = new float[480]; // 0.005 seconds
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);

        _source = new SampleSource(samples, _testConfig);
        _source.Loop = true;
        _source.Play();

        var buffer = new float[2400]; // Request more than source length

        // Act
        int framesRead = _source.ReadSamples(buffer, 1200);

        // Assert
        framesRead.Should().BeGreaterThan(0);
        _source.IsEndOfStream.Should().BeFalse(); // Should not end when looping
    }

    [Fact]
    public void Position_ShouldTrackPlaybackPosition()
    {
        // Arrange
        var samples = new float[48000]; // 0.5 seconds at 48kHz stereo
        _source = new SampleSource(samples, _testConfig);
        _source.Play();

        var buffer = new float[2400]; // 0.025 seconds

        // Act
        _source.ReadSamples(buffer, 1200);
        var position = _source.Position;

        // Assert
        position.Should().BeGreaterThan(0.0);
        position.Should().BeLessThanOrEqualTo(_source.Duration);
    }

    [Fact]
    public void Seek_ToValidPosition_ShouldReturnTrue()
    {
        // Arrange
        var samples = new float[48000]; // 0.5 seconds
        _source = new SampleSource(samples, _testConfig);

        // Act
        bool result = _source.Seek(0.25);

        // Assert
        result.Should().BeTrue();
        _source.Position.Should().BeApproximately(0.25, 0.01);
    }

    [Fact]
    public void Seek_ToInvalidPosition_ShouldReturnFalse()
    {
        // Arrange
        var samples = new float[4800];
        _source = new SampleSource(samples, _testConfig);

        // Act & Assert
        _source.Seek(-1.0).Should().BeFalse();
        _source.Seek(999.0).Should().BeFalse();
    }

    [Fact]
    public void SubmitSamples_WhenDynamicUpdateEnabled_ShouldUpdateData()
    {
        // Arrange
        _source = new SampleSource(1024, _testConfig);
        var newSamples = new float[2048];
        for (int i = 0; i < newSamples.Length; i++)
            newSamples[i] = 0.5f;

        // Act
        _source.SubmitSamples(newSamples);

        // Assert
        _source.Duration.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void SubmitSamples_WhenDynamicUpdateDisabled_ShouldThrow()
    {
        // Arrange
        var samples = new float[4800];
        _source = new SampleSource(samples, _testConfig);
        var newSamples = new float[1000];

        // Act
        Action act = () => _source.SubmitSamples(newSamples);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Dynamic sample submission*");
    }

    [Fact]
    public void Clear_ShouldResetDataAndPosition()
    {
        // Arrange
        var samples = new float[4800];
        _source = new SampleSource(samples, _testConfig);
        _source.Play();
        var buffer = new float[1024];
        _source.ReadSamples(buffer, 512);

        // Act
        _source.Clear();

        // Assert
        _source.Position.Should().Be(0.0);
        _source.Duration.Should().Be(0.0);
    }

    [Fact]
    public void Volume_ShouldAffectOutputAmplitude()
    {
        // Arrange
        var samples = new float[4800];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 1.0f; // Max amplitude

        _source = new SampleSource(samples, _testConfig);
        _source.Volume = 0.5f;
        _source.Play();

        var buffer = new float[1024];

        // Act
        _source.ReadSamples(buffer, 512);

        // Assert
        buffer.Where(s => s != 0f).Should().AllSatisfy(s =>
            Math.Abs(s).Should().BeLessThanOrEqualTo(0.6f)); // Should be attenuated
    }

    [Fact]
    public void StreamInfo_ShouldReflectConfiguration()
    {
        // Arrange
        var samples = new float[4800];
        _source = new SampleSource(samples, _testConfig);

        // Act
        var streamInfo = _source.StreamInfo;

        // Assert
        streamInfo.Channels.Should().Be(_testConfig.Channels);
        streamInfo.SampleRate.Should().Be(_testConfig.SampleRate);
    }

    [Theory]
    [InlineData(AudioState.Playing)]
    [InlineData(AudioState.Paused)]
    [InlineData(AudioState.Stopped)]
    public void State_Transitions_ShouldWorkCorrectly(AudioState targetState)
    {
        // Arrange
        var samples = new float[4800];
        _source = new SampleSource(samples, _testConfig);

        // Act
        switch (targetState)
        {
            case AudioState.Playing:
                _source.Play();
                break;
            case AudioState.Paused:
                _source.Play();
                _source.Pause();
                break;
            case AudioState.Stopped:
                _source.Play();
                _source.Stop();
                break;
        }

        // Assert
        _source.State.Should().Be(targetState);
    }

    [Fact]
    public void Dispose_ShouldReleaseResources()
    {
        // Arrange
        var samples = new float[4800];
        _source = new SampleSource(samples, _testConfig);

        // Act
        _source.Dispose();

        // Assert - Should throw ObjectDisposedException
        Action act = () => _source.ReadSamples(new float[100], 50);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void ConcurrentReadSamples_ShouldBeSafe()
    {
        // Arrange
        var samples = new float[48000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);

        _source = new SampleSource(samples, _testConfig);
        _source.Loop = true;
        _source.Play();

        var tasks = new List<Task>();

        // Act - Read from multiple threads
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var buffer = new float[1024];
                for (int j = 0; j < 10; j++)
                {
                    _source.ReadSamples(buffer, 512);
                }
            }));
        }

        // Assert
        Action act = () => Task.WaitAll(tasks.ToArray());
        act.Should().NotThrow();
    }
}
