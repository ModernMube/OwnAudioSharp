using System;
using System.IO;
using FluentAssertions;
using OwnaudioNET.Sources;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// The raw extraction path: whole-file and windowed reads, and the peak scan a waveform
/// display lives on. The fixture WAV carries a hard negative spike every 500 frames so a
/// bucket can be checked for the sign, not just the magnitude.
/// </summary>
public sealed class FileSourceDataExtractionTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int Frames = SampleRate * 2;
    private const int SpikePeriod = 500;

    private readonly string _wavPath;

    public FileSourceDataExtractionTests()
    {
        _wavPath = _writeTempWav();
    }

    /// <summary></summary>
    public void Dispose()
    {
        try { File.Delete(_wavPath); } catch { }
    }

    /// <summary>
    /// A null duration means the rest of the file, sample for sample.
    /// </summary>
    [Fact]
    public void FloatData_WithoutDuration_ReadsTheWholeFile()
    {
        using var _source = _open();

        float[] _data = _source.GetFloatAudioData(TimeSpan.Zero);

        _data.Length.Should().Be(Frames * Channels);
        _data[250 * Channels].Should().BeApproximately(-30000f / 32768f, 0.001f);
    }

    /// <summary>
    /// A duration cuts the read at exactly that many frames.
    /// </summary>
    [Fact]
    public void FloatData_WithDuration_StopsAtTheWindow()
    {
        using var _source = _open();

        float[] _data = _source.GetFloatAudioData(TimeSpan.Zero, TimeSpan.FromSeconds(0.5));

        _data.Length.Should().Be(SampleRate / 2 * Channels);
    }

    /// <summary>
    /// Reading from a position lands on that position's samples, not on the file's head.
    /// </summary>
    [Fact]
    public void FloatData_FromPosition_SkipsWhatCameBefore()
    {
        using var _source = _open();

        float[] _whole = _source.GetFloatAudioData(TimeSpan.Zero);
        float[] _tail = _source.GetFloatAudioData(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(0.5));

        int _offset = SampleRate * Channels;
        _tail.Length.Should().Be(SampleRate / 2 * Channels);
        _tail[0].Should().BeApproximately(_whole[_offset], 0.001f);
        _tail[1000].Should().BeApproximately(_whole[_offset + 1000], 0.001f);
    }

    /// <summary>
    /// The byte flavour is the same samples, just not typed.
    /// </summary>
    [Fact]
    public void ByteData_IsTheFloatDataReinterpreted()
    {
        using var _source = _open();

        float[] _floats = _source.GetFloatAudioData(TimeSpan.Zero, TimeSpan.FromSeconds(0.25));
        byte[] _bytes = _source.GetByteAudioData(TimeSpan.Zero, TimeSpan.FromSeconds(0.25));

        _bytes.Length.Should().Be(_floats.Length * sizeof(float));

        float[] _back = new float[_floats.Length];
        Buffer.BlockCopy(_bytes, 0, _back, 0, _bytes.Length);
        _back.Should().Equal(_floats);
    }

    /// <summary>
    /// Every bucket of the scan spans one spike, so the whole curve has to come back negative -
    /// a scan that took the absolute value would fail this.
    /// </summary>
    [Fact]
    public void Peaks_KeepTheSignOfTheLoudestSample()
    {
        using var _source = _open();
        int _points = Frames / SpikePeriod;

        float[] _peaks = _source.GetPeaks(_points);

        _peaks.Length.Should().Be(_points);
        _peaks.Should().OnlyContain(p => p < -0.9f);
    }

    /// <summary>
    /// A display asking for a handful of points gets a handful, not the file.
    /// </summary>
    [Fact]
    public void Peaks_HonourThePointCount()
    {
        using var _source = _open();

        _source.GetPeaks(10).Length.Should().Be(10);
        _source.GetPeaks(0).Should().BeEmpty();
        _source.GetPeaks(-1).Should().BeEmpty();
    }

    /// <summary>
    /// A point count the sample count is not divisible by still comes back whole - the tail
    /// bucket is short, not dropped.
    /// </summary>
    [Fact]
    public void Peaks_WithAnUnevenPointCount_KeepTheTailBucket()
    {
        using var _source = _open();

        _source.GetPeaks(7).Length.Should().Be(7);
        _source.GetPeaks(193).Length.Should().Be(193);
    }

    /// <summary>
    /// More points than there are samples caps at what the file can actually fill.
    /// </summary>
    [Fact]
    public void Peaks_AskedForMoreThanTheFileHas_StopAtTheSampleCount()
    {
        using var _source = _open();

        _source.GetPeaks(Frames * Channels * 2).Length.Should().Be(Frames * Channels);
    }

    /// <summary>
    /// A single point is the whole file's loudest sample, spikes included.
    /// </summary>
    [Fact]
    public void Peaks_WithOnePoint_IsTheLoudestSampleOfTheFile()
    {
        using var _source = _open();

        float[] _peaks = _source.GetPeaks(1);

        _peaks.Length.Should().Be(1);
        _peaks[0].Should().BeApproximately(-30000f / 32768f, 0.001f);
    }

    private FileSource _open() => new FileSource(_wavPath, 8192, SampleRate, Channels);

    /// <summary>
    /// 16 bit PCM, quiet ramp with a -30000 spike every SpikePeriod frames.
    /// </summary>
    private static string _writeTempWav()
    {
        string _path = Path.Combine(Path.GetTempPath(), $"ownaudio_extraction_{Guid.NewGuid():N}.wav");

        int _dataLen = Frames * Channels * 2;
        using var _fs = new FileStream(_path, FileMode.Create, FileAccess.Write);
        using var _w = new BinaryWriter(_fs);

        _w.Write(new[] { 'R', 'I', 'F', 'F' });
        _w.Write(36 + _dataLen);
        _w.Write(new[] { 'W', 'A', 'V', 'E' });
        _w.Write(new[] { 'f', 'm', 't', ' ' });
        _w.Write(16);
        _w.Write((ushort)1);
        _w.Write((ushort)Channels);
        _w.Write(SampleRate);
        _w.Write(SampleRate * Channels * 2);
        _w.Write((ushort)(Channels * 2));
        _w.Write((ushort)16);
        _w.Write(new[] { 'd', 'a', 't', 'a' });
        _w.Write(_dataLen);

        for (int i = 0; i < Frames; i++)
        {
            short _value = i % SpikePeriod == 250 ? (short)-30000 : (short)(i % 100);
            for (int c = 0; c < Channels; c++)
                _w.Write(_value);
        }

        return _path;
    }
}
