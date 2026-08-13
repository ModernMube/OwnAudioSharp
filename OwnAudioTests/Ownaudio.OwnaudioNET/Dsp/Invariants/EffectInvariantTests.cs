using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Interfaces;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Invariants;

/// <summary>
/// Rules every built-in effect has to obey, checked over the whole catalog. These catch
/// the boring-but-fatal stuff — state left over on a block boundary, a ramp that restarts,
/// a filter that blows up — without knowing anything about what the effect does.
/// </summary>
public class EffectInvariantTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;

    /// <summary>
    /// The single most useful test here: chopping the same signal into 512-frame blocks
    /// instead of one 8192-frame call must not change a single sample. Anything that
    /// forgets state at a block edge shows up immediately.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void BlockSizeDoesNotChangeTheOutput(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        int _frames = _case.BlockRateGain ? Rate * 4 : 8192;
        float[] _in = SignalGenerator.Sine(440, -12.0, _frames, Ch, Rate);

        float[] _oneShot, _chopped;
        using (IEffectProcessor _a = _case.Create()) { _oneShot = EffectHarness.Render(_a, _in, Ch, 2048); }
        using (IEffectProcessor _b = _case.Create()) { _chopped = EffectHarness.Render(_b, _in, Ch, 512); }

        if (_case.BlockRateGain)
        {
            //Per-block gain, so only the settled level can match
            int _tail = _frames - Rate;
            double _a = SignalMeasure.RmsDbOfFrames(_oneShot, Ch, _tail, Rate);
            double _b = SignalMeasure.RmsDbOfFrames(_chopped, Ch, _tail, Rate);

            Math.Abs(_a - _b).Should().BeLessThan(0.5,
                $"{name} runs its gain at block rate, but the settled level still has to land in the " +
                $"same place regardless of block size ({_a:F2} dB at 2048 frames vs {_b:F2} dB at 512)");
            return;
        }

        int _at = SignalMeasure.FirstDiff(_oneShot, _chopped, 1e-5);
        _at.Should().Be(-1,
            $"{name} must render identically at any block size, but sample {_at} differs " +
            $"({(_at >= 0 ? _oneShot[_at] : 0f)} vs {(_at >= 0 ? _chopped[_at] : 0f)}) — state is leaking across block boundaries");
    }

    /// <summary>
    /// Reset has to put the effect back where it started.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void ResetMakesTheEffectRepeatItself(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        float[] _in = SignalGenerator.Sine(440, -12.0, 4096, Ch, Rate);

        using (IEffectProcessor _fx = _case.Create())
        {
            float[] _first = EffectHarness.Render(_fx, _in, Ch);
            _fx.Reset();
            float[] _second = EffectHarness.RenderInto(_fx, _in, Ch);

            SignalMeasure.MaxDiff(_first, _second).Should().BeLessThan(1e-5,
                $"{name} should be deterministic after Reset()");
        }
    }

    /// <summary>
    /// Every Reset has to move the counter the mixer polls, otherwise the native twin
    /// keeps its tail while the managed object looks clean.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void ResetBumpsTheGeneration(string name)
    {
        using IEffectProcessor _fx = EffectCatalog.Get(name).Create();

        int _before = _fx.ResetGeneration;
        _fx.Reset();
        _fx.Reset();

        _fx.ResetGeneration.Should().Be(_before + 2,
            $"{name}.Reset() has to be visible to the rust-native mirror");
    }

    /// <summary>
    /// Disabled means untouched, bit for bit.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void DisabledEffectPassesAudioThrough(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        float[] _in = SignalGenerator.Sine(1000, -6.0, 4096, Ch, Rate);

        using (IEffectProcessor _fx = _case.Create())
        {
            _fx.Enabled = false;
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            SignalMeasure.MaxDiff(_in, _out).Should().Be(0.0, $"{name} is bypassed and must not alter the signal");
        }
    }

    /// <summary>
    /// Mix = 0 is the dry signal. Effects that ignore Mix are flagged in the catalog
    /// instead of being quietly skipped — see <see cref="MixDeviationsAreDocumented"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void MixZeroGivesBackTheDrySignal(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        if (!_case.MixHonored) return;

        float[] _in = SignalGenerator.Sine(1000, -6.0, 4096, Ch, Rate);

        using (IEffectProcessor _fx = _case.Create())
        {
            _fx.Mix = 0.0f;
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            SignalMeasure.MaxDiff(_in, _out).Should().BeLessThan(1e-6, $"{name} at Mix=0 must be fully dry");
        }
    }

    /// <summary>
    /// Nothing produces NaN or infinity, not even at full scale into a hot input.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void OutputStaysFiniteOnHotInput(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        float[] _in = SignalGenerator.Sine(200, 0.0, Rate, Ch, Rate);

        using (IEffectProcessor _fx = _case.Create())
        {
            float[] _out = EffectHarness.Render(_fx, _in, Ch);
            SignalMeasure.AllFinite(_out).Should().BeTrue($"{name} produced NaN or Inf on a full-scale sine");
        }
    }

    /// <summary>
    /// Same, but with broadband noise — different code paths light up than with a tone.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void OutputStaysFiniteOnNoise(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        float[] _in = SignalGenerator.Noise(-3.0, Rate, Ch);

        using (IEffectProcessor _fx = _case.Create())
        {
            float[] _out = EffectHarness.Render(_fx, _in, Ch);
            SignalMeasure.AllFinite(_out).Should().BeTrue($"{name} produced NaN or Inf on noise");
        }
    }

    /// <summary>
    /// Nothing may invent signal out of digital silence.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void SilenceInSilenceOut(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        if (!_case.SilenceStaysSilent) return;

        float[] _in = SignalGenerator.Silence(Rate, Ch);

        using (IEffectProcessor _fx = _case.Create())
        {
            float[] _out = EffectHarness.Render(_fx, _in, Ch);
            SignalMeasure.PeakDb(_out).Should().BeLessThan(-100.0, $"{name} generated something out of silence");
        }
    }

    /// <summary>
    /// Initializing twice with the same config must not change anything.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void SecondInitializeIsHarmless(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        float[] _in = SignalGenerator.Sine(440, -12.0, 4096, Ch, Rate);

        float[] _once, _twice;
        using (IEffectProcessor _a = _case.Create()) { _once = EffectHarness.Render(_a, _in, Ch); }
        using (IEffectProcessor _b = _case.Create())
        {
            _b.Initialize(EffectHarness.Config());
            _twice = EffectHarness.Render(_b, _in, Ch);
        }

        SignalMeasure.MaxDiff(_once, _twice).Should().BeLessThan(1e-5, $"{name} behaves differently after a second Initialize()");
    }

    /// <summary>
    /// Disposing twice is not an error.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void DisposeIsIdempotent(string name)
    {
        IEffectProcessor _fx = EffectCatalog.Get(name).Create();
        _fx.Initialize(EffectHarness.Config());
        _fx.Dispose();

        Action _again = () => _fx.Dispose();
        _again.Should().NotThrow($"{name} must survive a second Dispose()");
    }

    /// <summary>
    /// Every instance has its own id and a name to show in a UI.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void EachInstanceHasItsOwnIdentity(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);

        using (IEffectProcessor _a = _case.Create())
        using (IEffectProcessor _b = _case.Create())
        {
            _a.Name.Should().NotBeNullOrEmpty();
            _a.Id.Should().NotBe(_b.Id, $"two {name} instances must not share an id");
        }
    }

    /// <summary>
    /// Anywhere we relaxed an invariant there has to be a written reason. Keeps the
    /// known gaps visible rather than letting them rot into accepted behaviour.
    /// </summary>
    [Theory]
    [MemberData(nameof(EffectCatalog.Names), MemberType = typeof(EffectCatalog))]
    public void MixDeviationsAreDocumented(string name)
    {
        EffectCase _case = EffectCatalog.Get(name);
        if (_case.MixHonored && _case.SilenceStaysSilent) return;

        _case.Deviation.Should().NotBeNullOrWhiteSpace($"{name} relaxes an invariant without saying why");
    }
}
