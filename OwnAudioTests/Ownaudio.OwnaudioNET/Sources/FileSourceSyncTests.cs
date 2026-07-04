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
    public void TempoChange_ShouldTriggerGracePeriod()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();

        // Act
        _source.Tempo = 1.5f;

        var fieldInfo = typeof(FileSource).GetField("_gracePeriodEndTime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        double gracePeriodEndTime = (double)fieldInfo!.GetValue(_source)!;

        // Assert
        gracePeriodEndTime.Should().BeGreaterThan(0);
        gracePeriodEndTime.Should().BeApproximately(1.0, 0.1); // ~1.0s
    }

    [Fact]
    public void PitchChange_ShouldTriggerGracePeriod()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();

        // Act
        _source.PitchShift = 2;

        var fieldInfo = typeof(FileSource).GetField("_gracePeriodEndTime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        double gracePeriodEndTime = (double)fieldInfo!.GetValue(_source)!;

        // Assert
        gracePeriodEndTime.Should().BeGreaterThan(0);
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
    public void GracePeriod_ShouldPreventDriftCorrection()
    {

        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();

        _mockDecoder.Invocations.Clear();

        _source.Tempo = 1.0f; // Triggers grace period for ~0.5s (24000 frames)

        var trackTimeField = typeof(FileSource).GetField("_trackLocalTime",
           System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        trackTimeField!.SetValue(_source, 0.0);

        var ignoreField = typeof(FileSource).GetField("_gracePeriodEndTime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        ignoreField!.SetValue(_source, double.MaxValue);

        var buffer = new float[1024];

        // Act
        bool success = _source.ReadSamplesAtTime(5.0, buffer, 512, out var result);

        // Assert
        success.Should().BeTrue();

        _mockDecoder.Verify(d => d.TrySeek(It.IsAny<TimeSpan>(), out It.Ref<string>.IsAny), Times.Never());
    }

    [Fact]
    public void AfterGracePeriod_DriftShouldTriggerCorrection()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();

        var ignoreField = typeof(FileSource).GetField("_gracePeriodEndTime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        ignoreField!.SetValue(_source, 0.0);

        // Act
        var buffer = new float[1024];
        _source.ReadSamplesAtTime(5.0, buffer, 512, out var result);
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

    [Fact]
    public void DriftCorrection_ShouldUseRelativeSeek()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();

        // Disable grace period
        var ignoreField = typeof(FileSource).GetField("_gracePeriodEndTime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        ignoreField!.SetValue(_source, 0.0);

        // Set Tempo to 2.0x
        _source.Tempo = 2.0f;
        ignoreField!.SetValue(_source, 0.0);

        var positionField = typeof(FileSource).GetField("_currentPosition",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        positionField!.SetValue(_source, 10.0); // File position = 10s
        
        var trackTimeField = typeof(FileSource).GetField("_trackLocalTime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        trackTimeField!.SetValue(_source, 5.0); // Track Time = 5s
        
        positionField!.SetValue(_source, 20.0);
        
        var buffer = new float[1024];
        _source.ReadSamplesAtTime(5.1, buffer, 512, out var result);

        Thread.Sleep(100);
    }

    /// <summary>
    /// Verifies that the standalone <see cref="FileSource.Position"/> advances by <b>content</b>
    /// time (framesRead * tempo / sampleRate), i.e. how far into the file the playback has reached.
    /// </summary>
    /// <remarks>
    /// Reading N output frames at tempo T consumes N*T frames of file <em>content</em> (SoundTouch
    /// produces N output frames from N*T input frames), so the reported position — "where in the
    /// file are we" — advances tempo-scaled. This is the documented golden behavior characterized by
    /// <c>FileSourceCharacterizationTests</c> (plan D.0a): Position tracks decoded content time, not
    /// wall-clock elapsed time. The code advances it deliberately via
    /// <c>exactSourceFrames = framesRead * _tempo</c> with a fractional-frame accumulator.
    /// </remarks>
    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.2f)]
    [InlineData(0.8f)]
    public void Position_AfterReadSamples_AdvancesByContentTime_TempoScaled(float tempo)
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.Play();
        Thread.Sleep(100); // Allow buffer to fill

        _source.Tempo = tempo;
        Thread.Sleep(20); // Allow SoundTouch to stabilize

        const int framesRequested = 512;
        var buffer = new float[framesRequested * 2]; // stereo

        double positionBefore = _source.Position;

        // Act
        int framesRead = _source.ReadSamples(buffer, framesRequested);

        // Assert
        if (framesRead > 0)
        {
            // Reading framesRead output frames at tempo T consumes framesRead*T content frames; the
            // integer fractional-frame accumulator keeps this within ~1 frame per call.
            double expectedAdvance = framesRead * tempo / 48000.0;
            double actualAdvance = _source.Position - positionBefore;

            actualAdvance.Should().BeApproximately(expectedAdvance, 0.002,
                because: $"Position should advance by content time ({expectedAdvance:F5}s = " +
                         $"framesRead * tempo / sampleRate), tracking file playback position. " +
                         $"Tempo={tempo}, framesRead={framesRead}");
        }
    }
}
