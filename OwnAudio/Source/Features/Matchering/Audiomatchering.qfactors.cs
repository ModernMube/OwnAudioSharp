using System;
using Logger;

namespace OwnaudioNET.Features.Matchering;

/// <summary>
/// Q factor picking for the matching EQ.
/// </summary>
partial class AudioAnalyzer
{
    #region Q Factor Calculation

    /// <summary>
    /// Final Q per band. Mixes a psychoacoustic base Q with modifiers from the gain
    /// amount, how much the neighbouring bands agree, and the source/target density.
    /// </summary>
    private float[] _optimalQFactors(float[] eqAdjustments, AudioSpectrum sourceSpectrum, AudioSpectrum targetSpectrum)
    {
        var qFactors = new float[_freqBands.Length];

        Log.Info("Calculated Q factors:");

        for (int i = 0; i < _freqBands.Length; i++)
        {
            float freq = _freqBands[i];
            float baseQ = _freqBasedQ(freq);

            float q = _combineQ(baseQ,
                _gainBasedQ(Math.Abs(eqAdjustments[i])),
                _neighborQ(eqAdjustments, i),
                _spectralQ(sourceSpectrum.FrequencyBands[i], targetSpectrum.FrequencyBands[i]),
                freq);

            qFactors[i] = Math.Clamp(q, 2.5f, 8.0f);

            Log.Info($"{_bandNames[i]}: Q={qFactors[i]:F2} (Gain: {eqAdjustments[i]:+0.1;-0.1}dB)");
        }

        return qFactors;
    }

    #endregion

    #region Base Q Factor Calculations

    /// <summary>
    /// Base Q by frequency range - wider down low, roughly 1/3 octave through the mids.
    /// </summary>
    private float _freqBasedQ(float frequency)
    {
        return frequency switch
        {
            <= 40f => 3.0f,
            <= 80f => 3.5f,
            <= 160f => 3.8f,
            <= 315f => 4.0f,
            <= 630f => 4.2f,
            <= 1250f => 4.3f,
            <= 4000f => 4.3f,
            <= 10000f => 4.2f,
            _ => 4.0f
        };
    }

    /// <summary>
    /// Bigger correction, tighter bell. Kept conservative so it stays musical.
    /// </summary>
    private float _gainBasedQ(float gainAdjustment)
    {
        return gainAdjustment switch
        {
            <= 2.0f => 1.0f,
            <= 5.0f => 1.05f,
            <= 10.0f => 1.1f,
            _ => 1.2f
        };
    }

    #endregion

    #region Contextual Q Factor Analysis

    /// <summary>
    /// Looks at +/-2 bands around the current one. If they're moving the same way we
    /// can go wide, if the correction is isolated we go narrow. Closer bands weigh more.
    /// </summary>
    private float _neighborQ(float[] adjustments, int currentBand)
    {
        float currentGain = adjustments[currentBand];
        float correlation = 0f;
        int neighborCount = 0;

        for (int offset = -2; offset <= 2; offset++)
        {
            if (offset == 0) continue;

            int n = currentBand + offset;
            if (n < 0 || n >= adjustments.Length) continue;

            float neighborGain = adjustments[n];
            bool sameDirection = (currentGain > 0 && neighborGain > 0) || (currentGain < 0 && neighborGain < 0);

            if (sameDirection && Math.Abs(currentGain - neighborGain) < 3.0f)
                correlation += 1.0f / (Math.Abs(offset) + 1);

            neighborCount++;
        }

        float normalized = neighborCount > 0 ? correlation / neighborCount : 0f;

        return normalized switch
        {
            >= 0.7f => 0.7f,
            >= 0.5f => 0.8f,
            >= 0.3f => 0.9f,
            >= 0.1f => 1.1f,
            _ => 1.3f
        };
    }

    /// <summary>
    /// Sharp level differences between source and target want a narrow bell,
    /// broad shifts want a wide one.
    /// </summary>
    private float _spectralQ(float sourceLevel, float targetLevel)
    {
        float logRatio = (float)Math.Log10(targetLevel / Math.Max(sourceLevel, 1e-10f));

        return Math.Abs(logRatio) switch
        {
            <= 0.1f => 0.8f,
            <= 0.3f => 0.9f,
            <= 0.6f => 1.0f,
            <= 1.0f => 1.2f,
            _ => 1.4f
        };
    }

    #endregion

    #region Q Factor Combination

    /// <summary>
    /// Weighted mix of the four Q sources. Base Q drives it, lows lean on the neighbour
    /// context for smooth transitions, highs lean on the gain modifier for precision.
    /// </summary>
    private float _combineQ(float baseQ, float gainQ, float neighborQ, float spectralQ, float frequency)
    {
        float baseWeight = 0.6f;
        float gainWeight = 0.2f;
        float neighborWeight = 0.1f;
        float spectralWeight = 0.1f;

        if(frequency <= 250f)
        {
            neighborWeight += 0.1f;
            spectralWeight -= 0.05f;
        }
        else if (frequency >= 4000f)
        {
            gainWeight += 0.1f;
            neighborWeight -= 0.1f;
        }

        return baseQ * baseWeight +
               (baseQ * gainQ) * gainWeight +
               (baseQ * neighborQ) * neighborWeight +
               (baseQ * spectralQ) * spectralWeight;
    }

    #endregion
}
