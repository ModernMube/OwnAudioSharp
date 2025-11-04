using System;

namespace OwnaudioNET.Features.Matchering;

partial class AudioAnalyzer
{
    #region Q Factor Calculation

    /// <summary>
    /// Calculates optimal Q factors for each frequency band based on required corrections and spectral analysis.
    /// </summary>
    /// <param name="eqAdjustments">EQ adjustment values in dB for each band</param>
    /// <param name="sourceSpectrum">Source audio frequency spectrum</param>
    /// <param name="targetSpectrum">Target audio frequency spectrum</param>
    /// <returns>Array of optimized Q factors for each frequency band</returns>
    /// <remarks>
    /// This method combines multiple analysis techniques to determine optimal Q factors:
    /// - Psychoacoustic frequency-based Q values
    /// - Gain-dependent Q adjustments for surgical vs musical corrections
    /// - Neighboring band correlation analysis for smooth transitions
    /// - Spectral density differences for targeted corrections
    /// All factors are weighted and combined to produce musically appropriate Q values.
    /// </remarks>
    private float[] CalculateOptimalQFactors(float[] eqAdjustments, AudioSpectrum sourceSpectrum, AudioSpectrum targetSpectrum)
    {
        var qFactors = new float[FrequencyBands.Length];

        for (int i = 0; i < FrequencyBands.Length; i++)
        {
            float freq = FrequencyBands[i];
            float gainAdjustment = Math.Abs(eqAdjustments[i]);

            // Base Q factor based on frequency range (psychoacoustic considerations)
            float baseQ = GetFrequencyBasedQ(freq);

            // Adjustment based on required gain correction
            float gainBasedQ = CalculateGainBasedQ(gainAdjustment);

            // Adjustment based on neighboring bands correlation
            float neighborQ = CalculateNeighboringBandsQ(eqAdjustments, i);

            // Adjustment based on spectral density differences
            float spectralQ = CalculateSpectralDensityQ(sourceSpectrum.FrequencyBands[i], targetSpectrum.FrequencyBands[i]);

            // Combine all factors with weighted average
            qFactors[i] = CombineQFactors(baseQ, gainBasedQ, neighborQ, spectralQ, freq);

            // Clamp to reasonable limits
            qFactors[i] = Math.Max(0.3f, Math.Min(5.0f, qFactors[i]));
        }

        Console.WriteLine("Calculated Q factors:");
        var bandNames = new[] {
            "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
            "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
            "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
        };

        for (int i = 0; i < qFactors.Length; i++)
        {
            Console.WriteLine($"{bandNames[i]}: Q={qFactors[i]:F2} (Gain: {eqAdjustments[i]:+0.1;-0.1}dB)");
        }

        return qFactors;
    }

    #endregion

    #region Base Q Factor Calculations

    /// <summary>
    /// Determines base Q factor based on frequency range using psychoacoustic principles.
    /// </summary>
    /// <param name="frequency">Center frequency in Hz</param>
    /// <returns>Base Q factor optimized for the given frequency range</returns>
    /// <remarks>
    /// Uses psychoacoustic research to determine appropriate Q factors for different frequency ranges.
    /// Lower frequencies typically use wider Q factors (lower values) for more musical results,
    /// while mid-frequencies use moderate Q factors, and high frequencies use progressively
    /// narrower Q factors for precise control without harshness.
    /// </remarks>
    private float GetFrequencyBasedQ(float frequency)
    {
        return frequency switch
        {
            <= 40f => 0.5f,
            <= 80f => 0.6f,
            <= 160f => 0.7f,
            <= 315f => 0.8f,
            <= 630f => 1.0f,
            <= 1250f => 1.2f,
            <= 2000f => 1.3f,
            <= 2500f => 1.4f,
            <= 3150f => 1.5f,
            <= 4000f => 1.4f,
            <= 5000f => 1.2f,
            <= 6300f => 1.15f,   // Gently decreasing Q
            <= 8000f => 1.1f,    // Continuous decrease
            <= 10000f => 1.05f,  // Further decrease
            <= 12500f => 1.0f,   // Neutral Q
            _ => 0.95f           // Slightly wider at highest frequencies
        };
    }

    /// <summary>
    /// Calculates Q adjustment based on the amount of gain correction needed.
    /// </summary>
    /// <param name="gainAdjustment">Absolute gain adjustment in dB</param>
    /// <returns>Q factor modifier based on gain amount</returns>
    /// <remarks>
    /// Larger gain adjustments typically require narrower Q factors for more surgical correction
    /// while smaller adjustments can use wider Q factors for more musical results.
    /// This method provides a conservative approach to Q factor scaling to maintain musicality.
    /// </remarks>
    private float CalculateGainBasedQ(float gainAdjustment)
    {
        // Less aggressive Q increase even for large corrections
        return gainAdjustment switch
        {
            <= 1.0f => 1.0f,
            <= 2.0f => 1.05f,    
            <= 4.0f => 1.15f,    
            <= 6.0f => 1.25f,    
            <= 8.0f => 1.35f,    
            _ => 1.5f            
        };
    }

    #endregion

    #region Contextual Q Factor Analysis

    /// <summary>
    /// Analyzes neighboring frequency bands to determine if wide or narrow Q is more appropriate.
    /// </summary>
    /// <param name="adjustments">All EQ adjustments for context analysis</param>
    /// <param name="currentBand">Current band index being analyzed</param>
    /// <returns>Q factor modifier based on neighboring band correlation</returns>
    /// <remarks>
    /// Examines the correlation between the current band and its neighbors (±2 bands).
    /// High correlation (similar adjustments in neighboring bands) suggests using wider Q factors
    /// for more musical, smooth transitions. Low correlation suggests using narrower Q factors
    /// for more surgical, isolated corrections. Distance weighting gives closer neighbors more influence.
    /// </remarks>
    private float CalculateNeighboringBandsQ(float[] adjustments, int currentBand)
    {
        float currentGain = adjustments[currentBand];
        float correlation = 0f;
        int neighborCount = 0;

        // Check previous and next bands
        for (int offset = -2; offset <= 2; offset++)
        {
            if (offset == 0) continue;

            int neighborIndex = currentBand + offset;
            if (neighborIndex >= 0 && neighborIndex < adjustments.Length)
            {
                float neighborGain = adjustments[neighborIndex];

                // Calculate correlation (same direction and similar magnitude)
                float gainDifference = Math.Abs(currentGain - neighborGain);
                bool sameDirection = (currentGain > 0 && neighborGain > 0) || (currentGain < 0 && neighborGain < 0);

                if (sameDirection && gainDifference < 3.0f)
                {
                    correlation += 1.0f / (Math.Abs(offset) + 1); // Closer neighbors have more weight
                }

                neighborCount++;
            }
        }

        float normalizedCorrelation = neighborCount > 0 ? correlation / neighborCount : 0f;

        // High correlation with neighbors = use wider Q (more musical)
        // Low correlation = use narrower Q (more surgical)
        return normalizedCorrelation switch
        {
            >= 0.7f => 0.7f,     // Wide Q for highly correlated regions
            >= 0.5f => 0.8f,     // Moderate-wide Q for correlated regions
            >= 0.3f => 0.9f,     // Neutral Q for somewhat correlated regions
            >= 0.1f => 1.1f,     // Narrow Q for low correlation
            _ => 1.3f            // Narrower Q for isolated corrections
        };
    }

    /// <summary>
    /// Calculates Q factor based on spectral density differences between source and target spectrums.
    /// </summary>
    /// <param name="sourceLevel">Source frequency band level</param>
    /// <param name="targetLevel">Target frequency band level</param>
    /// <returns>Q factor modifier based on spectral density analysis</returns>
    /// <remarks>
    /// Analyzes the ratio between source and target levels to determine appropriate Q factor.
    /// Sharp spectral features (large level differences) benefit from narrower Q factors
    /// for precise correction, while broad spectral changes benefit from wider Q factors
    /// for more natural, musical results.
    /// </remarks>
    private float CalculateSpectralDensityQ(float sourceLevel, float targetLevel)
    {
        float ratio = targetLevel / Math.Max(sourceLevel, 1e-10f);
        float logRatio = (float)Math.Log10(ratio);

        // Sharp spectral features need narrower Q
        // Broad spectral changes need wider Q
        return Math.Abs(logRatio) switch
        {
            <= 0.1f => 0.8f,     // Wide for very similar levels
            <= 0.3f => 0.9f,     // Moderate-wide for similar levels
            <= 0.6f => 1.0f,     // Neutral for moderate differences
            <= 1.0f => 1.2f,     // Narrow for large differences
            _ => 1.4f            // Very narrow for extreme differences
        };
    }

    #endregion

    #region Q Factor Combination

    /// <summary>
    /// Combines all Q factor considerations with frequency-dependent weighting to produce the final Q value.
    /// </summary>
    /// <param name="baseQ">Base Q factor from frequency characteristics</param>
    /// <param name="gainQ">Q modifier from gain amount analysis</param>
    /// <param name="neighborQ">Q modifier from neighboring band correlation</param>
    /// <param name="spectralQ">Q modifier from spectral density analysis</param>
    /// <param name="frequency">Center frequency for frequency-dependent weighting</param>
    /// <returns>Final optimized Q factor combining all analysis methods</returns>
    /// <remarks>
    /// Uses weighted combination of all Q factor analysis methods with frequency-dependent
    /// weight adjustments. Low frequencies emphasize neighboring band correlation for smooth
    /// transitions, while high frequencies emphasize gain-based adjustments for surgical precision.
    /// The base psychoacoustic Q factor receives the highest weight across all frequencies.
    /// </remarks>
    private float CombineQFactors(float baseQ, float gainQ, float neighborQ, float spectralQ, float frequency)
    {
        // Weight the different factors based on frequency range
        float baseWeight = 0.4f;      // Base psychoacoustic Q is most important
        float gainWeight = 0.3f;      // Gain-based adjustment is second most important
        float neighborWeight = 0.2f;  // Neighboring band correlation
        float spectralWeight = 0.1f;  // Spectral density consideration

        // Adjust weights based on frequency range
        if (frequency <= 250f) // Low frequencies
        {
            neighborWeight += 0.1f; // More emphasis on smooth transitions
            spectralWeight -= 0.05f;
        }
        else if (frequency >= 4000f) // High frequencies
        {
            gainWeight += 0.1f; // More emphasis on surgical corrections
            neighborWeight -= 0.1f;
        }

        float combinedQ = baseQ * baseWeight +
                          (baseQ * gainQ) * gainWeight +
                          (baseQ * neighborQ) * neighborWeight +
                          (baseQ * spectralQ) * spectralWeight;

        return combinedQ;
    }

    #endregion
}
