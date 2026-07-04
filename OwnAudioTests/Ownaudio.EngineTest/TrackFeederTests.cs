using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Tracks;
using Ownaudio.Safe;

namespace Ownaudio.EngineTest;

/// <summary>
/// Integration tests for the managed decoder→track bridge (<see cref="TrackFeeder"/>) and the
/// native <see cref="MultiTrackSession.AddFileTrack"/> wiring (which returns a
/// <see cref="FileTrack"/>).  These stream a temporary WAV file into a track's lock-free feed
/// without opening an audio device, so the audio thread never drains the buffer.
/// </summary>
[TestClass]
public class TrackFeederTests
{
    private const int SampleRate = 48_000;
    private const ushort Channels = 2;

    [TestMethod]
    public void Feeder_FillsTrackBuffer_AndReportsEndOfStream()
    {
        // A short file (well under the ~2 s ring buffer) drains completely.
        using var wav = new TempWavFile(channels: Channels, SampleRate, frames: 12_000);
        using var session = new MultiTrackSession(SampleRate, Channels);

        var completed = new ManualResetEventSlim(false);
        TrackFeedEndReason reason = default;

        using var decoder = new StreamingAudioDecoder(wav.Path, SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        using var feeder = new TrackFeeder(decoder, track, leaveDecoderOpen: true);
        feeder.Completed += (_, e) =>
        {
            reason = e.Reason;
            completed.Set();
        };

        int capacityBefore = track.FreeSampleCount;
        feeder.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)), "feeder should finish a small file quickly");
        Assert.AreEqual(TrackFeedEndReason.EndOfStream, reason, "should end by reaching EOF");

        int expectedSamples = 12_000 * Channels;
        Assert.AreEqual(capacityBefore - expectedSamples, track.FreeSampleCount,
            "every decoded sample should have been pushed into the track feed");
    }

    [TestMethod]
    public void Feeder_BackpressuresOnFullBuffer_WithoutCompleting()
    {
        // A file larger than the ring buffer cannot be fully written until a
        // consumer drains it; with no audio device the feeder fills to capacity and
        // parks (non-blocking back-pressure) rather than completing or busy-erroring.
        using var wav = new TempWavFile(channels: Channels, SampleRate, frames: 300_000);
        using var session = new MultiTrackSession(SampleRate, Channels);

        var completed = new ManualResetEventSlim(false);
        using var decoder = new StreamingAudioDecoder(wav.Path, SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        using var feeder = new TrackFeeder(decoder, track, leaveDecoderOpen: true);
        feeder.Completed += (_, _) => completed.Set();

        feeder.Start();

        Assert.IsFalse(completed.Wait(TimeSpan.FromMilliseconds(500)),
            "feeder should still be back-pressured against the full buffer, not finished");
        Assert.AreEqual(0, track.FreeSampleCount, "buffer should be full");
        Assert.IsTrue(feeder.IsRunning, "pump should still be running while back-pressured");
    }

    [TestMethod]
    public void Stop_HaltsFeeding_WithStoppedReason()
    {
        using var wav = new TempWavFile(channels: Channels, SampleRate, frames: 300_000);
        using var session = new MultiTrackSession(SampleRate, Channels);

        var completed = new ManualResetEventSlim(false);
        TrackFeedEndReason reason = default;
        using var decoder = new StreamingAudioDecoder(wav.Path, SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        using var feeder = new TrackFeeder(decoder, track, leaveDecoderOpen: true);
        feeder.Completed += (_, e) =>
        {
            reason = e.Reason;
            completed.Set();
        };

        feeder.Start();
        Thread.Sleep(100);
        feeder.Stop();

        Assert.IsFalse(feeder.IsRunning, "pump should have stopped");
        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(2)), "Completed should fire after Stop");
        Assert.AreEqual(TrackFeedEndReason.Stopped, reason);
    }

    [TestMethod]
    public void Constructor_NullArguments_Throw()
    {
        using var wav = new TempWavFile(channels: Channels, SampleRate, frames: 1_000);
        using var session = new MultiTrackSession(SampleRate, Channels);
        using var decoder = new StreamingAudioDecoder(wav.Path, SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.ThrowsExactly<ArgumentNullException>(() => new TrackFeeder(null!, track));
        Assert.ThrowsExactly<ArgumentNullException>(() => new TrackFeeder(decoder, null!));
    }

    [TestMethod]
    public void Start_Twice_Throws()
    {
        using var wav = new TempWavFile(channels: Channels, SampleRate, frames: 300_000);
        using var session = new MultiTrackSession(SampleRate, Channels);
        using var decoder = new StreamingAudioDecoder(wav.Path, SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        using var feeder = new TrackFeeder(decoder, track, leaveDecoderOpen: true);

        feeder.Start();
        Assert.ThrowsExactly<InvalidOperationException>(() => feeder.Start());
    }

    [TestMethod]
    public void AddFileTrack_WiresNativeFileTrack_AndRegistersTrack()
    {
        using var wav = new TempWavFile(channels: Channels, SampleRate, frames: 8_000);
        using var session = new MultiTrackSession(SampleRate, Channels);

        FileTrack fileTrack = session.AddFileTrack(wav.Path);

        Assert.IsNotNull(fileTrack.Track, "file track should expose the created track");
        Assert.AreEqual(1, session.Tracks.Count, "the track should be registered in the session");
        Assert.AreSame(fileTrack.Track, session.Tracks[0]);
        Assert.IsFalse(fileTrack.IsFinished, "a freshly opened file track is not finished");

        // Disposing the session tears down the file track (and its native source) without throwing.
        session.Dispose();
        Assert.IsFalse(fileTrack.IsFinished, "a disposed file track reports not-finished");
    }

    [TestMethod]
    public void AddFileTrack_NullOrBlankPath_Throws()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        Assert.ThrowsExactly<ArgumentException>(() => session.AddFileTrack("   "));
    }

    #region Helpers

    /// <summary>
    /// Writes a temporary 16-bit PCM WAV file and removes it on dispose.
    /// </summary>
    private sealed class TempWavFile : IDisposable
    {
        public string Path { get; }

        public TempWavFile(int channels, int sampleRate, int frames)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ownaudio_feeder_test_{Guid.NewGuid():N}.wav");

            int dataLen = frames * channels * 2;
            int byteRate = sampleRate * channels * 2;
            short blockAlign = (short)(channels * 2);

            using var ms = new FileStream(Path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(ms);

            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataLen);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write((ushort)blockAlign);
            w.Write((ushort)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataLen);

            for (int i = 0; i < frames; i++)
            {
                short value = (short)((i % 1000) * 30);
                for (int c = 0; c < channels; c++)
                {
                    w.Write(value);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    #endregion
}
