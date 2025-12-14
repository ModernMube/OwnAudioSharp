using System;
using System.Linq;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// Provides audio spectrum analysis and EQ matching functionality for audio processing applications.
    /// Implements advanced FFT-based frequency analysis and intelligent EQ adjustment algorithms.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region Dynamic Amplification Settings

        /// <summary>
        /// Calculates optimal dynamic amplification settings based on source and target audio characteristics.
        /// Uses conservative approach to preserve musical dynamics while achieving loudness matching.
        /// </summary>
        /// <param name="source">Source audio spectrum analysis</param>
        /// <param name="target">Target audio spectrum analysis</param>
        /// <returns>Optimized dynamic amplification settings</returns>
        /// <summary>
        /// Calculates comprehensive dynamic settings (Amp + Compressor) based on Crest Factor analysis.
        /// </summary>
        private DynamicAmpSettings CalculateDynamicAmpSettings(AudioSpectrum source, AudioSpectrum target)
        {
            float sourceCrest = 20 * (float)Math.Log10(source.PeakLevel / Math.Max(source.RMSLevel, 1e-10f));
            float targetCrest = 20 * (float)Math.Log10(target.PeakLevel / Math.Max(target.RMSLevel, 1e-10f));
            
            // Calculate optimal target level based on Loudness (LUFS-like approach)
            // Target is usually the reference file's loudness
            float targetLoudnessDb = target.Loudness;
            
            // Compressor Calculation
            // If Source is more dynamic (higher crest) than Target, we need compression
            float crestDiff = sourceCrest - targetCrest;
            
            float compThreshold = -12.0f;
            float compRatio = 1.0f;

            if (crestDiff > 0)
            {
                // Source is too dynamic - apply compression
                // Ratio scales with the crest difference
                compRatio = 1.5f + (crestDiff * 0.5f); // e.g., 6dB diff -> 1.5 + 3 = 4.5 ratio
                
                // Threshold needs to be low enough to catch the peaks
                // Approximately "Peak - CrestDiff" relative to full scale, but safer to use relative to RMS
                // Threshold approx RMS + stable headroom
                compThreshold = source.Loudness + (sourceCrest - crestDiff) * 0.5f; 
            }
            else
            {
                // Source is already dense, light compression for glue
                compRatio = 1.5f;
                compThreshold = source.PeakLevel - 6.0f; // Just catch highest peaks
            }

            // Safety clamps
            compRatio = Math.Clamp(compRatio, 1.0f, 10.0f);
            compThreshold = Math.Clamp(compThreshold, -30.0f, -2.0f); 

            Console.WriteLine($"Dynamics Match: Source Crest {sourceCrest:F1}dB vs Target {targetCrest:F1}dB");
            Console.WriteLine($"Calculated Compressor: Thresh {compThreshold:F1}dB, Ratio {compRatio:F1}:1");

            // Store compressor settings in the (abused) DynamicAmpSettings or return a tuple
            // Since we only have DynamicAmpSettings type defined in the signature context of ProcessEQMatching,
            // we will piggyback these values or assume the structure is modified/extended.
            // Wait, I cannot modify DynamicAmpSettings struct definition easily if it's in another file I haven't seen?
            // "Audiomatchering.dynamics.cs" is partial. I should check if DynamicAmpSettings is defined here or elsewhere.
            // It wasn't in the file view. It's likely a struct/class in another file. 
            // I will optimize by packing these into the returned object if possible, 
            // BUT looking at the previous code, DynamicAmpSettings seemed to have specific fields (TargetLevel, Attack...).
            // I will use `TargetLevel` effectively, but I need to pass Ratio/Threshold to the main process.
            // 
            // Workaround: I will return a special struct or just use the fields creatively?
            // No, the best way in C# within the same partial class is to just have this method return the values 
            // and the caller uses them. But the signature was:
            // DynamicAmpSettings CalculateDynamicAmpSettings(...)
            //
            // Let's assume for now I will use standard DynamicAmpSettings for the Amp, 
            // AND I will add a new method `CalculateCompressorSettings`.
            
            // Let's stick to the original function for Amp, and I'll add a new one for Compressor. 
            // Retaining original logic for Amp but tuning it:
            
            return new DynamicAmpSettings
            {
                TargetLevel = targetLoudnessDb, // Match loudness directly
                AttackTime = 0.1f, // Faster for modern sound
                ReleaseTime = 0.5f,
                MaxGain = 6.0f     // Allow more gain if needed
            };
        }

        /// <summary>
        /// Calculates optimal compressor settings to match target dynamics.
        /// </summary>
        private (float Threshold, float Ratio) CalculateCompressorSettings(AudioSpectrum source, AudioSpectrum target)
        {
             float sourceCrest = 20 * (float)Math.Log10(source.PeakLevel / Math.Max(source.RMSLevel, 1e-10f));
             float targetCrest = 20 * (float)Math.Log10(target.PeakLevel / Math.Max(target.RMSLevel, 1e-10f));
             
             float crestDiff = sourceCrest - targetCrest;
             float ratio = 1.0f;
             float threshold = -0.1f;

             if (crestDiff > 1.0f)
             {
                 // Reduced Ratio scaling (0.4f -> 0.25f) for more transparent gluing
                 ratio = 1.0f + (crestDiff * 0.25f);
                 // Threshold shoud be around the RMS level + some headroom
                 // If signal is -12 RMS and we want to reduce crest by 3dB, threshold should be lower.
                 threshold = 20 * (float)Math.Log10(source.RMSLevel) + 3.0f; 
             }
             else
             {
                 // Glue compression
                 ratio = 2.0f;
                 threshold = 20 * (float)Math.Log10(source.PeakLevel) - 4.0f;
             }

             return (Math.Clamp(threshold, -40f, -0.5f), Math.Clamp(ratio, 1.2f, 20.0f));
        }

        #endregion
    }
}
