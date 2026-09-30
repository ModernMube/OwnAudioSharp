using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Log-domain compressor on the rust engine: look-ahead, soft knee, auto release, stereo link, mid/side.
/// Setters push straight down to the native effect, getters read our copy.
/// </summary>
public sealed class OwnCompressorEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamThreshold = 2;
    private const uint ParamRatio = 3;
    private const uint ParamKnee = 4;
    private const uint ParamAttack = 5;
    private const uint ParamRelease = 6;
    private const uint ParamAutoRelease = 7;
    private const uint ParamLookahead = 8;
    private const uint ParamRmsDetector = 9;
    private const uint ParamFeedbackTopology = 10;
    private const uint ParamStereoLink = 11;
    private const uint ParamMidSide = 12;
    private const uint ParamSidechainHighPass = 13;
    private const uint ParamMakeup = 14;
    private const uint ParamAutoMakeup = 15;
    private const uint ParamRange = 16;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 1.0f;
    private float _threshold = -18.0f;
    private float _ratio = 4.0f;
    private float _knee = 6.0f;
    private float _attack = 10.0f;
    private float _release = 100.0f;
    private bool _autoRelease;
    private float _lookahead = 0.0f;
    private bool _rmsDetector;
    private bool _feedbackTopology;
    private float _stereoLink = 1.0f;
    private bool _midSide;
    private float _sidechainHighPass = 0.0f;
    private float _makeup = 0.0f;
    private bool _autoMakeup;
    private float _range = 60.0f;

    internal OwnCompressorEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Properties

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.OwnCompressor;

    /// <summary>
    /// Bypass switch.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>
    /// Parallel mix, 0.0 - 1.0.
    /// </summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; _setParam(ParamMix, value); }
    }

    /// <summary>
    /// Threshold in dBFS, -60 - 0.
    /// </summary>
    public float Threshold
    {
        get => _threshold;
        set { _threshold = value; _setParam(ParamThreshold, value); }
    }

    /// <summary>
    /// Ratio, 1 - 100.
    /// </summary>
    public float Ratio
    {
        get => _ratio;
        set { _ratio = value; _setParam(ParamRatio, value); }
    }

    /// <summary>
    /// Knee width in dB, 0 - 24.
    /// </summary>
    public float Knee
    {
        get => _knee;
        set { _knee = value; _setParam(ParamKnee, value); }
    }

    /// <summary>
    /// Attack in ms (t90), 0.01 - 300.
    /// </summary>
    public float Attack
    {
        get => _attack;
        set { _attack = value; _setParam(ParamAttack, value); }
    }

    /// <summary>
    /// Release in ms (t90), 5 - 5000.
    /// </summary>
    public float Release
    {
        get => _release;
        set { _release = value; _setParam(ParamRelease, value); }
    }

    /// <summary>
    /// Programme dependent release.
    /// </summary>
    public bool AutoRelease
    {
        get => _autoRelease;
        set { _autoRelease = value; _setParam(ParamAutoRelease, value ? 1f : 0f); }
    }

    /// <summary>
    /// Look-ahead in ms, 0 - 10. This is also the effect's latency.
    /// </summary>
    public float Lookahead
    {
        get => _lookahead;
        set { _lookahead = value; _setParam(ParamLookahead, value); }
    }

    /// <summary>
    /// RMS instead of peak detection.
    /// </summary>
    public bool RmsDetector
    {
        get => _rmsDetector;
        set { _rmsDetector = value; _setParam(ParamRmsDetector, value ? 1f : 0f); }
    }

    /// <summary>
    /// Feedback instead of feed-forward detection.
    /// </summary>
    public bool FeedbackTopology
    {
        get => _feedbackTopology;
        set { _feedbackTopology = value; _setParam(ParamFeedbackTopology, value ? 1f : 0f); }
    }

    /// <summary>
    /// Stereo link, 0.0 - 1.0.
    /// </summary>
    public float StereoLink
    {
        get => _stereoLink;
        set { _stereoLink = value; _setParam(ParamStereoLink, value); }
    }

    /// <summary>
    /// Mid/side instead of left/right processing.
    /// </summary>
    public bool MidSide
    {
        get => _midSide;
        set { _midSide = value; _setParam(ParamMidSide, value ? 1f : 0f); }
    }

    /// <summary>
    /// Sidechain high-pass in Hz, 0 (off) or 20 - 500.
    /// </summary>
    public float SidechainHighPass
    {
        get => _sidechainHighPass;
        set { _sidechainHighPass = value; _setParam(ParamSidechainHighPass, value); }
    }

    /// <summary>
    /// Makeup gain in dB, -24 - +24.
    /// </summary>
    public float Makeup
    {
        get => _makeup;
        set { _makeup = value; _setParam(ParamMakeup, value); }
    }

    /// <summary>
    /// Automatic makeup gain.
    /// </summary>
    public bool AutoMakeup
    {
        get => _autoMakeup;
        set { _autoMakeup = value; _setParam(ParamAutoMakeup, value ? 1f : 0f); }
    }

    /// <summary>
    /// Maximum gain reduction in dB, 0 - 60.
    /// </summary>
    public float Range
    {
        get => _range;
        set { _range = value; _setParam(ParamRange, value); }
    }

    #endregion

    /// <summary>
    /// Drops the native effect handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    private void _setParam(uint paramId, float value)
    {
        if (_disposed) return;
        OwnAudioNative.ownaudio_v1_effect_set_param(_mixerHandle, _handle.DangerousGetHandle(), paramId, value);
    }
}
