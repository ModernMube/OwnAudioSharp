using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;
using OwnaudioNET.Interfaces;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Contract;

/// <summary>
/// Runs the shared DSP contract against the managed effects. The same file is checked
/// on the native side by ownaudio-core/tests/dsp_contract.rs, so both implementations
/// answer to one written spec instead of to each other.
/// </summary>
public class DspContractTests
{
    private static readonly JsonDocument Contract = _load();

    private static JsonDocument _load()
    {
        string _path = Path.Combine(AppContext.BaseDirectory, "dsp-contract.json");
        return JsonDocument.Parse(File.ReadAllText(_path));
    }

    /// <summary>
    /// One flattened case per row, so a failure names the effect, the parameters and the
    /// tone that broke rather than just "contract failed".
    /// </summary>
    public static IEnumerable<object[]> Cases
    {
        get
        {
            JsonElement _effects = Contract.RootElement.GetProperty("effects");
            for (int e = 0; e < _effects.GetArrayLength(); e++)
            {
                JsonElement _entry = _effects[e];
                JsonElement _cases = _entry.GetProperty("cases");

                for (int c = 0; c < _cases.GetArrayLength(); c++)
                    yield return new object[] { e, c, _label(_entry, _cases[c]) };
            }
        }
    }

    private static string _label(JsonElement entry, JsonElement kase)
    {
        string _params = entry.GetProperty("params").ToString();
        return $"{entry.GetProperty("effect").GetString()} {_params} @ {kase.GetProperty("freqHz").GetDouble():F0}Hz " +
               $"{kase.GetProperty("inputDb").GetDouble():F0}dB -> {kase.GetProperty("measure").GetString()}";
    }

    /// <summary>
    /// Builds the effect the contract entry describes, feeds it the tone and checks the
    /// measurement lands inside the stated tolerance.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void ManagedEffectMeetsTheContract(int effectIndex, int caseIndex, string label)
    {
        JsonElement _entry = Contract.RootElement.GetProperty("effects")[effectIndex];
        JsonElement _kase = _entry.GetProperty("cases")[caseIndex];

        int _rate = Contract.RootElement.GetProperty("sampleRate").GetInt32();
        int _channels = Contract.RootElement.GetProperty("channels").GetInt32();
        double _settle = Contract.RootElement.GetProperty("settleSeconds").GetDouble();
        double _window = Contract.RootElement.GetProperty("measureSeconds").GetDouble();

        double _freq = _kase.GetProperty("freqHz").GetDouble();
        double _inputDb = _kase.GetProperty("inputDb").GetDouble();
        string _measure = _kase.GetProperty("measure").GetString()!;
        double _expect = _kase.GetProperty("expect").GetDouble();
        double _tolerance = _kase.GetProperty("tolerance").GetDouble();

        int _settleFrames = (int)(_settle * _rate);
        int _frames = _settleFrames + (int)(_window * _rate);

        float[] _in = SignalGenerator.Sine(_freq, _inputDb, _frames, _channels, _rate);

        double _actual;
        using (IEffectProcessor _fx = _build(_entry, _rate))
        {
            float[] _out = EffectHarness.Render(_fx, _in, _channels, EffectHarness.BlockFrames, _rate);

            float[] _wet = EffectHarness.Steady(_out, _channels, 0, _settleFrames);
            float[] _dry = EffectHarness.Steady(_in, _channels, 0, _settleFrames);

            _actual = _measure switch
            {
                "peakDb" => SignalMeasure.PeakDb(_wet),
                "rmsDb" => SignalMeasure.RmsDb(_wet),
                "gainDb" => SignalMeasure.MagnitudeDbAt(_wet, _freq, _rate) - SignalMeasure.MagnitudeDbAt(_dry, _freq, _rate),
                _ => throw new InvalidOperationException($"unknown measure '{_measure}'")
            };
        }

        string _note = _kase.TryGetProperty("note", out JsonElement _n) ? $" — {_n.GetString()}" : string.Empty;

        _actual.Should().BeApproximately(_expect, _tolerance,
            $"{label} must land at {_expect:F2} dB ±{_tolerance:F2}, measured {_actual:F2} dB{_note}");
    }

    private static IEffectProcessor _build(JsonElement entry, int rate)
    {
        JsonElement _p = entry.GetProperty("params");

        switch (entry.GetProperty("effect").GetString())
        {
            case "limiter":
                return new LimiterEffect(rate,
                    (float)_num(_p, "thresholdDb", -3.0),
                    (float)_num(_p, "ceilingDb", -0.1),
                    (float)_num(_p, "releaseMs", 50.0),
                    (float)_num(_p, "lookaheadMs", 5.0));

            case "compressor":
            {
                var _fx = new CompressorEffect(CompressorPreset.Default, rate);
                _fx.Threshold = (float)_num(_p, "thresholdDb", -20.0);
                _fx.Ratio = (float)_num(_p, "ratio", 4.0);
                _fx.KneeWidth = (float)_num(_p, "kneeDb", 0.0);
                _fx.AttackTime = (float)_num(_p, "attackMs", 5.0);
                _fx.ReleaseTime = (float)_num(_p, "releaseMs", 200.0);
                _fx.MakeupGain = (float)_num(_p, "makeupDb", 0.0);
                return _fx;
            }

            case "equalizer":
            {
                var _fx = new EqualizerEffect(rate);
                for (int b = 0; b < 10; b++)
                {
                    if (_p.TryGetProperty($"band{b}Db", out JsonElement _g))
                        _fx.SetBandGain(b, Eq10Centres[b], 1.0f, (float)_g.GetDouble());
                }
                return _fx;
            }

            case "equalizer30":
            {
                var _fx = new Equalizer30BandEffect(rate);
                for (int b = 0; b < 30; b++)
                {
                    if (_p.TryGetProperty($"band{b}Db", out JsonElement _g))
                        _fx[b] = (float)_g.GetDouble();
                }
                return _fx;
            }

            default:
                throw new InvalidOperationException($"contract names an effect the runner does not build: {entry.GetProperty("effect")}");
        }
    }

    private static double _num(JsonElement obj, string name, double fallback)
        => obj.TryGetProperty(name, out JsonElement _v) ? _v.GetDouble() : fallback;

    private static readonly float[] Eq10Centres =
    {
        31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f
    };
}
