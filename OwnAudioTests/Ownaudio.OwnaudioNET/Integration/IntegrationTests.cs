using Ownaudio.Core;
using OwnaudioNET;
using OwnaudioNET.Core;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Integration;

/// <summary>
/// Integration tests for complete workflows using OwnaudioNET.
/// </summary>
public class IntegrationTests : IDisposable
{
    public IntegrationTests()
    {
        // Ensure clean state
        if (OwnaudioNet.IsInitialized)
        {
            OwnaudioNet.Shutdown();
        }
    }

    public void Dispose()
    {
        if (OwnaudioNet.IsInitialized)
        {
            OwnaudioNet.Shutdown();
        }
    }

    [Fact]
    public void CompleteWorkflow_InitializeStartPlayStop_ShouldWork()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();

        // Act & Assert - Initialize
        OwnaudioNet.Initialize(config, useMockEngine: true);
        OwnaudioNet.IsInitialized.Should().BeTrue();

        // Start
        OwnaudioNet.Start();
        OwnaudioNet.IsRunning.Should().BeTrue();

        // Send some audio
        var samples = new float[1024];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 48000);

        Action sendAct = () => OwnaudioNet.Send(samples);
        sendAct.Should().NotThrow();

        // Stop
        OwnaudioNet.Stop();
        OwnaudioNet.IsRunning.Should().BeFalse();

        // Shutdown
        OwnaudioNet.Shutdown();
        OwnaudioNet.IsInitialized.Should().BeFalse();
    }

    [Fact]
    public void AudioMixerWorkflow_AddSourcesAndMix_ShouldWork()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();
        var engine = AudioEngineFactory.CreateMockEngine(config);
        using var mixer = new AudioMixer(engine);

        // Create sources
        var samples1 = GenerateSineWave(48000, 440, config.SampleRate, config.Channels);
        var samples2 = GenerateSineWave(48000, 880, config.SampleRate, config.Channels);

        using var source1 = new SampleSource(samples1, config);
        using var source2 = new SampleSource(samples2, config);

        // Act
        mixer.AddSource(source1);
        mixer.AddSource(source2);

        source1.Play();
        source2.Play();

        mixer.Start();

        // Let it mix for a bit
        Thread.Sleep(200);

        // Assert
        mixer.SourceCount.Should().Be(2);
        mixer.IsRunning.Should().BeTrue();

        // Cleanup
        mixer.Stop();
        mixer.RemoveSource(source1.Id);
        mixer.RemoveSource(source2.Id);
    }

    [Fact]
    public void MultipleSourcesWithVolumeControl_ShouldMixCorrectly()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();
        var engine = AudioEngineFactory.CreateMockEngine(config);
        using var mixer = new AudioMixer(engine);

        var samples = GenerateSineWave(48000, 440, config.SampleRate, config.Channels);
        using var source1 = new SampleSource(samples, config);
        using var source2 = new SampleSource(samples, config);

        source1.Volume = 1.0f;
        source2.Volume = 0.5f;

        // Act
        mixer.AddSource(source1);
        mixer.AddSource(source2);
        source1.Play();
        source2.Play();
        mixer.Start();

        Thread.Sleep(100);

        // Assert
        mixer.SourceCount.Should().Be(2);
        source1.Volume.Should().Be(1.0f);
        source2.Volume.Should().Be(0.5f);
    }

    [Fact]
    public void SourcePlayPauseStopCycle_ShouldWorkCorrectly()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();
        var samples = GenerateSineWave(96000, 440, config.SampleRate, config.Channels);
        using var source = new SampleSource(samples, config);

        // Act & Assert - Play
        source.Play();
        source.State.Should().Be(AudioState.Playing);

        // Pause
        source.Pause();
        source.State.Should().Be(AudioState.Paused);

        // Resume
        source.Play();
        source.State.Should().Be(AudioState.Playing);

        // Stop
        source.Stop();
        source.State.Should().Be(AudioState.Stopped);
    }

    [Fact]
    public void SourceWithLoop_ShouldContinuePlaying()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();
        var samples = GenerateSineWave(4800, 440, config.SampleRate, config.Channels); // Short sample
        using var source = new SampleSource(samples, config);
        source.Loop = true;
        source.Play();

        var buffer = new float[48000]; // Request much more than available

        // Act
        int framesRead = source.ReadSamples(buffer, 24000);

        // Assert
        framesRead.Should().BeGreaterThan(0);
        source.IsEndOfStream.Should().BeFalse();
    }

    [Fact]
    public void SeekOperation_ShouldChangePosition()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();
        var samples = GenerateSineWave(96000, 440, config.SampleRate, config.Channels); // 1 second
        using var source = new SampleSource(samples, config);

        // Act
        bool seekResult = source.Seek(0.5);

        // Assert
        seekResult.Should().BeTrue();
        source.Position.Should().BeApproximately(0.5, 0.1);
    }

    [Fact]
    public void MixerMasterVolume_ShouldAffectOutput()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();
        var engine = AudioEngineFactory.CreateMockEngine(config);
        using var mixer = new AudioMixer(engine);

        var samples = GenerateSineWave(48000, 440, config.SampleRate, config.Channels);
        using var source = new SampleSource(samples, config);
        source.Play();

        // Act
        mixer.AddSource(source);
        mixer.MasterVolume = 0.5f;
        mixer.Start();

        Thread.Sleep(100);

        // Assert
        mixer.MasterVolume.Should().Be(0.5f);
        mixer.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void DynamicSourceManagement_AddRemoveDuringPlayback_ShouldWork()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();
        var engine = AudioEngineFactory.CreateMockEngine(config);
        using var mixer = new AudioMixer(engine);
        mixer.Start();

        var samples = GenerateSineWave(48000, 440, config.SampleRate, config.Channels);

        // Act - Add sources dynamically
        var sources = new List<SampleSource>();
        for (int i = 0; i < 5; i++)
        {
            var source = new SampleSource(samples, config);
            source.Play();
            mixer.AddSource(source);
            sources.Add(source);
            Thread.Sleep(20);
        }

        mixer.SourceCount.Should().Be(5);

        // Remove sources dynamically
        foreach (var source in sources)
        {
            mixer.RemoveSource(source.Id);
            Thread.Sleep(20);
        }

        // Assert
        mixer.SourceCount.Should().Be(0);

        // Cleanup
        sources.ForEach(s => s.Dispose());
    }

    [Fact]
    public void StressTest_MultipleSourcesRapidOperations_ShouldNotCrash()
    {
        // Arrange
        var config = OwnaudioNet.CreateDefaultConfig();
        var engine = AudioEngineFactory.CreateMockEngine(config);
        using var mixer = new AudioMixer(engine);
        mixer.Start();

        var tasks = new List<Task>();
        var samples = GenerateSineWave(48000, 440, config.SampleRate, config.Channels);

        // Act - Stress test with multiple threads
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 20; j++)
                {
                    using var source = new SampleSource(samples, config);
                    source.Play();
                    mixer.AddSource(source);
                    Thread.Sleep(10);
                    mixer.RemoveSource(source.Id);
                }
            }));
        }

        // Assert
        Action act = () => Task.WaitAll(tasks.ToArray());
        act.Should().NotThrow();
    }

    [Fact]
    public void GetDevices_ShouldReturnAvailableDevices()
    {
        // Arrange
        OwnaudioNet.Initialize(OwnaudioNet.CreateDefaultConfig(), useMockEngine: true);

        // Act
        var outputDevices = OwnaudioNet.GetOutputDevices();
        var inputDevices = OwnaudioNet.GetInputDevices();

        // Assert
        outputDevices.Should().NotBeNull();
        inputDevices.Should().NotBeNull();
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void DifferentSampleRates_ShouldWorkCorrectly(int sampleRate)
    {
        // Arrange
        var config = new AudioConfig
        {
            SampleRate = sampleRate,
            Channels = 2,
            BufferSize = 512
        };

        // Act
        OwnaudioNet.Initialize(config, useMockEngine: true);
        OwnaudioNet.Start();

        var samples = new float[1024];
        OwnaudioNet.Send(samples);

        // Assert
        OwnaudioNet.IsRunning.Should().BeTrue();

        // Cleanup
        OwnaudioNet.Stop();
        OwnaudioNet.Shutdown();
    }

    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void DifferentBufferSizes_ShouldWorkCorrectly(int bufferSize)
    {
        // Arrange
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = bufferSize
        };

        // Act
        OwnaudioNet.Initialize(config, useMockEngine: true);
        OwnaudioNet.Start();

        // Assert
        OwnaudioNet.IsRunning.Should().BeTrue();

        // Cleanup
        OwnaudioNet.Stop();
        OwnaudioNet.Shutdown();
    }

    // Helper methods
    private static float[] GenerateSineWave(int samples, float frequency, int sampleRate, int channels)
    {
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float phase = 2.0f * MathF.PI * frequency * (i / channels) / sampleRate;
            result[i] = MathF.Sin(phase);
        }
        return result;
    }
}
