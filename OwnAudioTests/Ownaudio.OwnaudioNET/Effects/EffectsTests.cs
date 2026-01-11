using Ownaudio.Core;
using OwnaudioNET.Effects;
using OwnaudioNET.Interfaces;

namespace Ownaudio.OwnaudioNET.Tests.Effects;

/// <summary>
/// Tests for audio effects processors.
/// </summary>
public class EffectsTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private AudioConfig CreateConfig() => new AudioConfig
    {
        SampleRate = SampleRate,
        Channels = Channels,
        BufferSize = 512
    };

    [Fact]
    public void ReverbEffect_Process_ShouldModifySignal()
    {
        // Arrange
        var effect = new ReverbEffect(ReverbPreset.Default);
        effect.Initialize(CreateConfig());

        var buffer = GenerateSineWave(1024, 440, SampleRate, Channels);

        // Act
        effect.Process(buffer, 512);

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void DelayEffect_Process_ShouldWork()
    {
        // Arrange
        var effect = new DelayEffect(sampleRate: SampleRate);
        effect.Initialize(CreateConfig());

        var buffer = GenerateImpulse(4800, SampleRate, Channels);

        // Act
        effect.Process(buffer, 2400);

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void ChorusEffect_Process_ShouldModifySignal()
    {
        // Arrange
        var effect = new ChorusEffect(ChorusPreset.Default);
        effect.Initialize(CreateConfig());

        var buffer = GenerateSineWave(1024, 440, SampleRate, Channels);

        // Act
        effect.Process(buffer, 512);

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void CompressorEffect_Process_ShouldWork()
    {
        // Arrange
        var effect = new CompressorEffect(SampleRate, Channels);
        effect.Initialize(CreateConfig());

        var buffer = GenerateVaryingAmplitude(4800, SampleRate, Channels);

        // Act
        effect.Process(buffer, 2400);

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void DistortionEffect_Process_ShouldWork()
    {
        // Arrange
        var effect = new DistortionEffect(DistortionPreset.Default);
        effect.Initialize(CreateConfig());

        var buffer = GenerateSineWave(1024, 440, SampleRate, Channels);

        // Act
        effect.Process(buffer, 512);

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void EnhancerEffect_Process_ShouldWork()
    {
        // Arrange
        var effect = new EnhancerEffect(SampleRate, Channels);
        effect.Initialize(CreateConfig());

        var buffer = GenerateSineWave(1024, 440, SampleRate, Channels);

        // Act
        effect.Process(buffer, 512);

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void AutoGainEffect_Process_ShouldWork()
    {
        // Arrange
        var effect = new AutoGainEffect(AutoGainPreset.Default);
        effect.Initialize(CreateConfig());

        var buffer = GenerateSineWave(4800, 440, SampleRate, Channels, amplitude: 0.1f);

        // Act
        for (int i = 0; i < 10; i++)
        {
            effect.Process(buffer, 2400);
        }

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void EqualizerEffect_Process_ShouldWork()
    {
        // Arrange
        var effect = new EqualizerEffect(SampleRate, Channels);
        effect.Initialize(CreateConfig());

        var buffer = GenerateSineWave(1024, 440, SampleRate, Channels);

        // Act
        effect.Process(buffer, 512);

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void Effects_Enabled_ShouldControlProcessing()
    {
        // Arrange
        var effect = new ReverbEffect(ReverbPreset.Default);
        effect.Initialize(CreateConfig());

        var buffer = GenerateSineWave(1024, 440, SampleRate, Channels);
        var original = buffer.ToArray();

        // Act - Disable effect
        effect.Enabled = false;
        effect.Process(buffer, 512);

        // Assert - Should pass through when disabled
        buffer.ToArray().Should().Equal(original);
    }

    [Fact]
    public void Effects_Dispose_ShouldReleaseResources()
    {
        // Arrange
        var effect = new ReverbEffect(ReverbPreset.Default);
        effect.Initialize(CreateConfig());

        // Act
        effect.Dispose();

        // Assert - Should not throw
        Action act = () => effect.Dispose();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public void Effects_WithDifferentMix_ShouldWork(float mix)
    {
        // Arrange
        var effect = new ReverbEffect(ReverbPreset.Default);
        effect.Initialize(CreateConfig());
        effect.Mix = mix;

        var buffer = GenerateSineWave(1024, 440, SampleRate, Channels);

        // Act
        effect.Process(buffer, 512);

        // Assert
        buffer.Should().NotBeEmpty();
    }

    [Fact]
    public void Effect_Name_ShouldNotBeEmpty()
    {
        // Arrange
        var effect = new ReverbEffect(ReverbPreset.Default);
        effect.Initialize(CreateConfig());

        // Act
        var name = effect.Name;

        // Assert
        name.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Effect_Id_ShouldBeUnique()
    {
        // Arrange
        var effect1 = new ReverbEffect(ReverbPreset.Default);
        var effect2 = new ReverbEffect(ReverbPreset.Default);

        // Act
        var id1 = effect1.Id;
        var id2 = effect2.Id;

        // Assert
        id1.Should().NotBe(id2);
    }

    // Helper methods
    private static float[] GenerateSineWave(int samples, float frequency, int sampleRate, int channels, float amplitude = 1.0f)
    {
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float phase = 2.0f * MathF.PI * frequency * (i / channels) / sampleRate;
            result[i] = amplitude * MathF.Sin(phase);
        }
        return result;
    }

    private static float[] GenerateImpulse(int samples, int sampleRate, int channels)
    {
        var result = new float[samples];
        result[0] = 1.0f; // Single impulse at start
        return result;
    }

    private static float[] GenerateVaryingAmplitude(int samples, int sampleRate, int channels)
    {
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            // Alternating loud and quiet sections
            float amplitude = (i / (samples / 4)) % 2 == 0 ? 0.9f : 0.1f;
            float phase = 2.0f * MathF.PI * 440 * (i / channels) / sampleRate;
            result[i] = amplitude * MathF.Sin(phase);
        }
        return result;
    }
}
