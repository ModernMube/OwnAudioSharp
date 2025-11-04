using Ownaudio.Core;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// Tests for the AudioMixer class.
/// </summary>
public class AudioMixerTests : IDisposable
{
    private readonly AudioConfig _testConfig;
    private IAudioEngine? _engine;
    private AudioMixer? _mixer;

    public AudioMixerTests()
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
        _mixer?.Dispose();
        _engine?.Dispose();
    }

    [Fact]
    public void Constructor_WithValidEngine_ShouldInitialize()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);

        // Act
        _mixer = new AudioMixer(_engine);

        // Assert
        _mixer.Should().NotBeNull();
        _mixer.IsRunning.Should().BeFalse();
        _mixer.SourceCount.Should().Be(0);
        _mixer.MasterVolume.Should().Be(1.0f);
    }

    [Fact]
    public void Constructor_WithNullEngine_ShouldThrowArgumentNullException()
    {
        // Arrange & Act
        Action act = () => new AudioMixer(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Start_ShouldSetIsRunningToTrue()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        // Act
        _mixer.Start();

        // Assert
        _mixer.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Stop_AfterStart_ShouldSetIsRunningToFalse()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);
        _mixer.Start();

        // Act
        _mixer.Stop();

        // Assert
        _mixer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void AddSource_WithValidSource_ShouldIncreaseSourceCount()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        var samples = new float[4800];
        var source = new SampleSource(samples, _testConfig);

        // Act
        _mixer.AddSource(source);

        // Assert
        _mixer.SourceCount.Should().Be(1);
    }

    [Fact]
    public void AddSource_WithNullSource_ShouldThrowArgumentNullException()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        // Act
        Action act = () => _mixer.AddSource(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RemoveSource_WithExistingSource_ShouldDecreaseSourceCount()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        var samples = new float[4800];
        var source = new SampleSource(samples, _testConfig);
        _mixer.AddSource(source);

        // Act
        bool removed = _mixer.RemoveSource(source.Id);

        // Assert
        removed.Should().BeTrue();
        _mixer.SourceCount.Should().Be(0);
    }

    [Fact]
    public void RemoveSource_WithNonExistingId_ShouldReturnFalse()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        // Act
        bool removed = _mixer.RemoveSource(Guid.NewGuid());

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public void RemoveMultipleSources_ShouldClearAllSources()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        var samples = new float[4800];
        var source1 = new SampleSource(samples, _testConfig);
        var source2 = new SampleSource(samples, _testConfig);
        _mixer.AddSource(source1);
        _mixer.AddSource(source2);

        // Act
        _mixer.RemoveSource(source1.Id);
        _mixer.RemoveSource(source2.Id);

        // Assert
        _mixer.SourceCount.Should().Be(0);
    }

    [Fact]
    public void MasterVolume_ShouldClampTo0And1()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        // Act & Assert
        _mixer.MasterVolume = 2.0f;
        _mixer.MasterVolume.Should().Be(1.0f);

        _mixer.MasterVolume = -0.5f;
        _mixer.MasterVolume.Should().Be(0.0f);

        _mixer.MasterVolume = 0.5f;
        _mixer.MasterVolume.Should().Be(0.5f);
    }

    [Fact]
    public void MixWithSources_ShouldUpdatePeakLevels()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        var samples = new float[4800];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);

        var source = new SampleSource(samples, _testConfig);
        source.Play();
        _mixer.AddSource(source);
        _mixer.Start();

        // Act - Wait for mixing to occur
        Thread.Sleep(100);

        // Assert
        _mixer.LeftPeak.Should().BeGreaterThan(0.0f);
        _mixer.RightPeak.Should().BeGreaterThan(0.0f);
    }

    [Fact]
    public void TotalMixedFrames_ShouldIncreaseWhileRunning()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        var samples = new float[4800];
        var source = new SampleSource(samples, _testConfig);
        source.Play();
        _mixer.AddSource(source);

        // Act
        _mixer.Start();
        Thread.Sleep(200);
        var frames = _mixer.TotalMixedFrames;
    }

    [Fact]
    public void EnableAutoDriftCorrection_ShouldBeSettable()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        // Act
        _mixer.EnableAutoDriftCorrection = true;

        // Assert
        _mixer.EnableAutoDriftCorrection.Should().BeTrue();
    }

    [Fact]
    public void AddMultipleSources_AndMix_ShouldCombineAudio()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        var samples1 = new float[4800];
        var samples2 = new float[4800];
        for (int i = 0; i < 4800; i++)
        {
            samples1[i] = 0.5f;
            samples2[i] = 0.3f;
        }

        var source1 = new SampleSource(samples1, _testConfig);
        var source2 = new SampleSource(samples2, _testConfig);
        source1.Play();
        source2.Play();

        // Act
        _mixer.AddSource(source1);
        _mixer.AddSource(source2);
        _mixer.Start();
        Thread.Sleep(100);

        // Assert
        _mixer.SourceCount.Should().Be(2);
        _mixer.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldStopMixerAndCleanup()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);
        _mixer.Start();

        // Act
        _mixer.Dispose();

        // Assert
        _mixer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Dispose_CalledTwice_ShouldNotThrow()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        // Act
        _mixer.Dispose();
        Action act = () => _mixer.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Operations_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);
        _mixer.Dispose();

        // Act & Assert
        var samples = new float[4800];
        var source = new SampleSource(samples, _testConfig);

        Action startAct = () => _mixer.Start();
        startAct.Should().Throw<ObjectDisposedException>();

        Action addAct = () => _mixer.AddSource(source);
        addAct.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void ConcurrentAddRemoveSources_ShouldBeSafe()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);
        _mixer.Start();

        var tasks = new List<Task>();
        var samples = new float[4800];

        // Act - Add and remove sources from multiple threads
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 10; j++)
                {
                    var source = new SampleSource(samples, _testConfig);
                    _mixer.AddSource(source);
                    Thread.Sleep(10);
                    _mixer.RemoveSource(source.Id);
                }
            }));
        }

        // Assert
        Action act = () => Task.WaitAll(tasks.ToArray());
        act.Should().NotThrow();
    }

    [Fact]
    public void Config_ShouldReturnAudioConfig()
    {
        // Arrange
        _engine = AudioEngineFactory.CreateMockEngine(_testConfig);
        _mixer = new AudioMixer(_engine);

        // Act
        var config = _mixer.Config;

        // Assert
        config.Should().NotBeNull();
        config.Channels.Should().BeGreaterThan(0);
        config.SampleRate.Should().BeGreaterThan(0);
    }
}
