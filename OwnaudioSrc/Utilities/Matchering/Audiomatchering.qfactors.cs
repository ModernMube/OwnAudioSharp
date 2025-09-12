using System;

namespace Ownaudio.Utilities.Matchering;

partial class AudioAnalyzer
{
   /// <summary>
   /// Calculates optimal Q factors for each frequency band based on required corrections and spectral analysis
   /// </summary>
   /// <param name="eqAdjustments">EQ adjustment values in dB for each band</param>
   /// <param name="sourceSpectrum">Source audio frequency spectrum</param>
   /// <param name="targetSpectrum">Target audio frequency spectrum</param>
   /// <returns>Array of optimized Q factors for each frequency band</returns>
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

   /// <summary>
   /// Gets base Q factor based on frequency range and psychoacoustic properties
   /// </summary>
   /// <param name="frequency">Center frequency in Hz</param>
   /// <returns>Base Q factor optimized for the frequency range</returns>
   private float GetFrequencyBasedQ(float frequency)
   {
      return frequency switch
      {
         <= 40f => 0.5f,      // Very wide for deep sub-bass (room acoustics)
         <= 80f => 0.6f,      // Wide for sub-bass (less precise perception)
         <= 160f => 0.7f,     // Moderate for low-bass
         <= 315f => 0.8f,     // Standard for bass range
         <= 630f => 1.0f,     // Neutral for low-mid
         <= 1250f => 1.2f,    // Slightly narrow for mid-range clarity
         <= 2500f => 1.1f,    // Moderate for upper-mid (vocal range)
         <= 5000f => 1.0f,    // Standard for presence range
         <= 8000f => 0.9f,    // Slightly wider for brilliance
         <= 12500f => 0.8f,   // Wider for upper brilliance
         _ => 0.7f            // Wide for air frequencies
      };
   }

   /// <summary>
   /// Calculates Q adjustment based on the amount of gain correction needed
   /// </summary>
   /// <param name="gainAdjustment">Absolute gain adjustment in dB</param>
   /// <returns>Q factor modifier based on gain amount</returns>
   private float CalculateGainBasedQ(float gainAdjustment)
   {
      // Larger corrections need narrower Q to be more surgical
      return gainAdjustment switch
      {
         <= 1.0f => 1.0f,     // No Q change for small corrections
         <= 2.0f => 1.1f,     // Slightly narrower for small corrections
         <= 4.0f => 1.3f,     // More focused for moderate corrections
         <= 6.0f => 1.5f,     // Narrow for large corrections
         <= 8.0f => 1.7f,     // Very narrow for very large corrections
         _ => 2.0f            // Maximum narrowness for extreme corrections
      };
   }

   /// <summary>
   /// Analyzes neighboring bands to determine if wide or narrow Q is more appropriate
   /// </summary>
   /// <param name="adjustments">All EQ adjustments</param>
   /// <param name="currentBand">Current band index</param>
   /// <returns>Q factor modifier based on neighboring band correlation</returns>
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
   /// Calculates Q based on spectral density differences between source and target
   /// </summary>
   /// <param name="sourceLevel">Source frequency band level</param>
   /// <param name="targetLevel">Target frequency band level</param>
   /// <returns>Q factor modifier based on spectral density</returns>
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

   /// <summary>
   /// Combines all Q factor considerations with frequency-dependent weighting
   /// </summary>
   /// <param name="baseQ">Base Q from frequency characteristics</param>
   /// <param name="gainQ">Q modifier from gain amount</param>
   /// <param name="neighborQ">Q modifier from neighboring band correlation</param>
   /// <param name="spectralQ">Q modifier from spectral density</param>
   /// <param name="frequency">Center frequency for weighting</param>
   /// <returns>Final optimized Q factor</returns>
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
}
