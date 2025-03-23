using System;

namespace Ownaudio.Utilities.Extensions;

internal static class NumberExtensions
{
    public static TimeSpan Milliseconds(this double value)
    {
        return TimeSpan.FromMilliseconds(value);
    }

    public static float VerifyVolume(this float volume)
    {
        return volume switch
        {
            > 1.0f => 1.0f,
            < 0.0f => 0.0f,
            _ => volume
        };
    }

    public static double VerifyTempo(this double tempo)
    {
        return tempo switch
        {
            > 20.0f => 20.0f,
            < -20.0f => -20.0f,
            _ => tempo
        };
    }

    public static double VerifyRate(this double rate)
    {
        return rate switch
        {
            > 20.0f => 20.0f,
            < -20.0f => -20.0f,
            _ => rate
        };
    }

    public static double VerifyPitch(this double pitch)
    {
        return pitch switch
        {
            > 6.0f => 6.0f,
            < -6.0f => -6.0f,
            _ => pitch
        };
    }

    #region Enhancer parameteres
    public static float VerifyEnhancerLowGain(this float lowgain)
    {
        return lowgain switch
        {
            > 2.0f => 2.0f,
            < 0.0f => 0.0f,
            _ => lowgain
        };
    }

    public static float VerifyEnhancerMidGain(this float midgain)
    {
        return midgain switch
        {
            > 2.0f => 2.0f,
            < 0.0f => 0.0f,
            _ => midgain
        };
    }

    public static float VerifyEnhancerHighGain(this float highgain)
    {
        return highgain switch
        {
            > 2.0f => 2.0f,
            < 0.0f => 0.0f,
            _ => highgain
        };
    }

    public static float VerifyEnhancerDrive(this float drive)
    {
        return drive switch
        {
            > 2.0f => 2.0f,
            < 0.0f => 0.0f,
            _ => drive
        };
    }
    #endregion

    #region DynamicAmp parameters
    public static int VerifyDynamicAmpTimeInterval(this int timeInterval)
    {
        return timeInterval switch
        {
            > 200 => 200,
            < 10 => 10,
            _ => timeInterval
        };
    }

    public static float VerifyDynamicAmpTargetVolume(this float targetVolume)
    {
        return targetVolume switch
        {
            > 1.0f => 1.0f,
            < 0.0f => 0.0f,
            _ => targetVolume
        };
    }

    public static float VerifyDynamicAmpAttackTime(this float attackTime)
    {
        return attackTime switch
        {
            > 1000.0f => 1000.0f,
            < 0.1f => 0.1f,
            _ => attackTime
        };
    }

    public static float VerifyDynamicAmpReleaseTime(this float releaseTime)
    {
        return releaseTime switch
        {
            > 5000.0f => 5000.0f,
            < 1.0f => 1.0f,
            _ => releaseTime
        };
    }

    public static float VerifyDynamicAmpThreshold(this float threshold)
    {
        return threshold switch
        {
            > 1.0f => 1.0f,
            < 0.0f => 0.0f,
            _ => threshold
        };
    }
    #endregion
}
