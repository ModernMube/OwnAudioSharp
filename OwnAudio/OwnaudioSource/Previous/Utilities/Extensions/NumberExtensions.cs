using System;

namespace OwnaudioLegacy.Utilities.Extensions;

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
}
