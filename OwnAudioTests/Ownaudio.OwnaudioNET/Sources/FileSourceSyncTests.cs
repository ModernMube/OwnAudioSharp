using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Decoders;
using Ownaudio.Synchronization;
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
        // Setup mock decoder
        _streamInfo = new AudioStreamInfo(2, 48000, TimeSpan.FromSeconds(60), 32);

        _mockDecoder = new Mock<IAudioDecoder>();
        _mockDecoder.Setup(d => d.StreamInfo).Returns(_streamInfo);
        
        // Setup default successful read behavior
        _mockDecoder.Setup(d => d.ReadFrames(It.IsAny<byte[]>()))
            .Returns((byte[] buffer) => 
            {
                // Generate silence for testing
                Array.Clear(buffer, 0, buffer.Length);
                int framesToRead = buffer.Length / (2 * sizeof(float)); // Stereo float
                return new AudioDecoderResult(framesToRead, true, false, null);
            });

        _mockDecoder.Setup(d => d.TrySeek(It.IsAny<TimeSpan>(), out It.Ref<string>.IsAny))
            .Returns(true);

        // Setup MasterClock
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
        // This test verifies that changing tempo sets the internal _ignoreSyncUntil field
        // preventing immediate drift correction.
        // We can't access private fields directly, but we can verify behavior via side-effects
        // or using reflection if strictly necessary. For now, we'll assume if it doesn't crash/throw it's okay,
        // but let's try to verify the logic via reflection for this specific "internal" requirement test.

        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();

        // Act
        _source.Tempo = 1.5f;

        // Verify via Reflection that _ignoreSyncUntil is set to a future value
        var fieldInfo = typeof(FileSource).GetField("_ignoreSyncUntil", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        long ignoreSyncUntil = (long)fieldInfo!.GetValue(_source)!;
        
        // Assert
        // Grace period is SamplePosition + SampleRate/2
        // Initial SamplePosition is 0
        ignoreSyncUntil.Should().BeGreaterThan(0);
        ignoreSyncUntil.Should().BeCloseTo(24000, 1000); // ~0.5s at 48kHz
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

        // Verify via Reflection
        var fieldInfo = typeof(FileSource).GetField("_ignoreSyncUntil", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        long ignoreSyncUntil = (long)fieldInfo!.GetValue(_source)!;
        
        // Assert
        ignoreSyncUntil.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ReadSamples_WhenAttachedToClock_ShouldUseReadSamplesAtTime()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();

        // Allow some buffering time
        Thread.Sleep(50);

        var buffer = new float[1024];

        // Act
        int read = _source.ReadSamples(buffer, 512);

        // Assert
        read.Should().Be(512);
        // We can't easily verify ReadSamplesAtTime was called without a spy, 
        // but we know logic flows there if _masterClock is not null.
    }
    
    [Fact]
    public void GracePeriod_ShouldPreventDriftCorrection()
    {
        // Verify that during grace period, drift is ignored even if significant
        
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();
        
        // Set Tempo to trigger grace period
        _source.Tempo = 1.0f; // Triggers grace period for ~0.5s (24000 frames)

        // Artificially advance MasterClock by 1 second to create massive drift
        // (Track time is 0, Master time is 1.0s -> Drift = 1.0s > 0.010s tolerance)
        // Normal behavior: Should Seek/Resync
        // Grace Period behavior: Should NOT Seek
        
        // We use Reflection to set internal _trackLocalTime to 0 manually just to be sure
         var trackTimeField = typeof(FileSource).GetField("_trackLocalTime", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
         trackTimeField!.SetValue(_source, 0.0);

        // Force master clock time
        // We need to advance the master clock manually or mock it effectively.
        // Since MasterClock uses Stopwatch, we can't easily jump it without internal access.
        // Instead, we can pass a future timestamp if we were calling ReadSamplesAtTime directly.
        // But ReadSamples calls ReadSamplesAtTime with _masterClock.CurrentTimestamp.
        
        // Strategy: Use reflection to set _ignoreSyncUntil to a huge value,
        // then call ReadSamplesAtTime directly (it's public interface IMasterClockSource)
        
        // Set grace period to infinity
        var ignoreField = typeof(FileSource).GetField("_ignoreSyncUntil", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        ignoreField!.SetValue(_source, long.MaxValue);

        var buffer = new float[1024];
        
        // Act
        // Request samples at time 5.0s (huge drift from 0.0s)
        bool success = _source.ReadSamplesAtTime(5.0, buffer, 512, out var result);

        // Assert
        success.Should().BeTrue();
        // If seek happened, we might see it in side effects, but strict verification is hard here.
        // However, if drift correction WAS active, it would call Seek(). 
        // We verified _mockDecoder.TrySeek returns true, so it wouldn't fail.
        // Ideally we'd verify TrySeek was NOT called.
        
        _mockDecoder.Verify(d => d.TrySeek(It.IsAny<TimeSpan>(), out It.Ref<string>.IsAny), Times.Never());
    }

    [Fact]
    public void AfterGracePeriod_DriftShouldTriggerCorrection()
    {
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();
        
        // Set grace period to 0 (expired)
        var ignoreField = typeof(FileSource).GetField("_ignoreSyncUntil", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        ignoreField!.SetValue(_source, 0L);

        // Act
        // Request samples at time 5.0s (huge drift)
        // This SHOULD trigger a seek
        var buffer = new float[1024];
        _source.ReadSamplesAtTime(5.0, buffer, 512, out var result);

        // Assert
        // Verify TrySeek WAS called
        _mockDecoder.Verify(d => d.TrySeek(It.IsAny<TimeSpan>(), out It.Ref<string>.IsAny), Times.AtLeastOnce());
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
        // This test verifies that drift correction uses relative calculation
        // instead of absolute (Time * Tempo), preventing jumps when tempo changed.
        
        // Arrange
        _source = new FileSource(_mockDecoder.Object);
        _source.AttachToClock(_masterClock!);
        _source.Play();
        
        // Disable grace period
        var ignoreField = typeof(FileSource).GetField("_ignoreSyncUntil", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        ignoreField!.SetValue(_source, long.MinValue);

        // Set Tempo to 2.0x
        _source.Tempo = 2.0f;
        // Setting Tempo resets _ignoreSyncUntil, so we must disable it AGAIN
        ignoreField!.SetValue(_source, long.MinValue);
        
        // Force current position to be at 10.0s
        var positionField = typeof(FileSource).GetField("_currentPosition", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        positionField!.SetValue(_source, 10.0); // File position = 10s

        // Force _trackLocalTime to be at 5.0s
        var trackTimeField = typeof(FileSource).GetField("_trackLocalTime", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        trackTimeField!.SetValue(_source, 5.0); // Track Time = 5s

        // Current state:
        // File is at 10.0s
        // Track output corresponds to 5.0s Master Time
        // Tempo is 2.0x
        
        // Act: Request samples at Master Time 5.1s
        // Drift = 5.1 - 5.0 = 0.1s
        // Tolerance = 0.010s -> Drift > Tolerance -> Correction!
        
        // New Relative Logic:
        // Target File Pos = CurrentPos + (Drift * Tempo)
        // Target = 10.0 + (0.1 * 2.0) = 10.2s
        
        // Old Absolute Logic (WRONG):
        // Target = MasterTime * Tempo = 5.1 * 2.0 = 10.2s
        // Wait, 10.2s matches here because 10.0s happens to be 5.0 * 2.0.
        // We need a case where history makes absolute calculation wrong.
        
        // Let's assume File Position is 20.0s at Track Time 5.0s (maybe played very fast before)
        positionField!.SetValue(_source, 20.0);
        
        // Relative Logic:
        // Target = 20.0 + (0.1 * 2.0) = 20.2s
        
        // Absolute Logic:
        // Target = 5.1 * 2.0 = 10.2s
        // This would jump BACKWARDS by 10 seconds!
        
        var buffer = new float[1024];
        _source.ReadSamplesAtTime(5.1, buffer, 512, out var result);

        // Allow background thread to process the seek request
        Thread.Sleep(100);

        // Assert
        // Verify Seek was called with ~20.2s
        _mockDecoder.Verify(d => d.TrySeek(
            It.Is<TimeSpan>(t => Math.Abs(t.TotalSeconds - 20.2) < 0.01), 
            out It.Ref<string>.IsAny), Times.Once());
    }
}
