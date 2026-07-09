using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Decoders;
using OwnaudioNET.Synchronization;
using OwnaudioNET.Sources;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// Tests for FileSource synchronization features, including MasterClock integration
/// and the "Always SoundTouch" logic.
/// </summary>
public class FileSourceSyncTests : IDisposable
{
    private readonly Mock<IAudioDecoder> _mockDecoder;
    private readonly AudioStreamInfo _streamInfo;
    private FileSource? _source;
    private MasterClock? _masterClock;

    public FileSourceSyncTests()
    {
        _streamInfo = new AudioStreamInfo(2, 48000, TimeSpan.FromSeconds(60), 32);

        _mockDecoder = new Mock<IAudioDecoder>();
        _mockDecoder.Setup(d => d.StreamInfo).Returns(_streamInfo);

        _mockDecoder.Setup(d => d.ReadFrames(It.IsAny<byte[]>()))
            .Returns((byte[] buffer) =>
            {
                Array.Clear(buffer, 0, buffer.Length);
                int framesToRead = buffer.Length / (2 * sizeof(float)); // Stereo float
                return new AudioDecoderResult(framesToRead, true, false, null);
            });

        _mockDecoder.Setup(d => d.TrySeek(It.IsAny<TimeSpan>(), out It.Ref<string>.IsAny))
            .Returns(true);

        _masterClock = new MasterClock(48000, 2);
    }

    public void Dispose()
    {
        _source?.Dispose();
        _masterClock?.Dispose();
    }

    [Fact]
    public void Constructor_WithMockDecoder_ShouldInitialize()
    {
        // Act
        _source = new FileSource(_mockDecoder.Object);

        // Assert
        _source.Should().NotBeNull();
        _source.Config.SampleRate.Should().Be(48000);
        _source.Duration.Should().Be(60.0);
    }

    [Fact]
    public void AttachToClock_ShouldSetIsSynchronized()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);

        // Act
        _source.AttachToClock(_masterClock!);

        // Assert
        _source.IsSynchronized.Should().BeTrue();
        _source.IsAttachedToClock.Should().BeTrue();
    }

    [Fact]
    public void DetachFromClock_ShouldResetIsSynchronized()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);

        // Act
        _source.DetachFromClock();

        // Assert
        _source.IsSynchronized.Should().BeFalse();
        _source.IsAttachedToClock.Should().BeFalse();
    }

    [Fact]
    public void ReadSamples_WhenAttachedToClock_ShouldUseReadSamplesAtTime()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();

        Thread.Sleep(50);

        var buffer = new float[1024];

        // Act
        int read = _source.ReadSamples(buffer, 512);

        // Assert
        read.Should().Be(512);
    }

    [Fact]
    public void SyncTolerance_ShouldBeConfigurable()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);

        // Act
        _source.SyncTolerance = 0.050; // 50ms

        // Assert
        _source.SyncTolerance.Should().Be(0.050);
    }

    /// <summary>
    /// Verifies that for a source without a native backend (mock decoder), the analysis cursor
    /// exposed by <see cref="FileSource.Position"/> advances by the raw number of decoded frames,
    /// independent of <see cref="FileSource.Tempo"/>.
    /// </summary>
    /// <remarks>
    /// As of 4.0 (plan L) tempo/pitch are applied natively on playback; <see cref="FileSource.ReadSamples"/>
    /// decodes raw PCM on demand for analysis and advances the position by the frames actually
    /// produced, so the advance is tempo-independent (a stretched playback rate does not scale the
    /// analysis cursor).
    /// </remarks>
    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.2f)]
    [InlineData(0.8f)]
    public void Position_AfterReadSamples_AdvancesByRawDecodedFrames(float tempo)
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.Play();
        _source.Tempo = tempo;

        const int framesRequested = 512;
        var buffer = new float[framesRequested * 2]; // stereo

        double positionBefore = _source.Position;

        // Act
        int framesRead = _source.ReadSamples(buffer, framesRequested);

        // Assert
        framesRead.Should().Be(framesRequested);

        double expectedAdvance = framesRead / 48000.0;
        double actualAdvance = _source.Position - positionBefore;

        actualAdvance.Should().BeApproximately(expectedAdvance, 0.002,
            because: $"the analysis cursor advances by raw decoded frames regardless of Tempo={tempo}");
    }
}
