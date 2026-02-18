using Microsoft.ML.OnnxRuntime;
using Logger;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class SimpleAudioSeparationService
    {
        #region Private Methods - Initialization

        /// <summary>
        /// Automatically detect model dimensions from ONNX metadata
        /// </summary>
        private void AutoDetectModelDimensions()
        {
            if (_onnxSession == null) return;

            try
            {
                var inputMetadata = _onnxSession.InputMetadata;
                if (inputMetadata.ContainsKey("input"))
                {
                    var inputShape = inputMetadata["input"].Dimensions;

                    if (inputShape.Length >= 4)
                    {
                        int expectedFreq = (int)inputShape[2];
                        int expectedTime = (int)inputShape[3];

                        Log.Info($"Model expects: Frequency={expectedFreq}, Time={expectedTime}");

                        if (expectedFreq != _modelParams.DimF || expectedTime != _modelParams.DimT)
                        {
                            Log.Info("Auto-adjusting model parameters to match ONNX model...");
                            int newDimT = (int)Math.Log2(expectedTime);

                            _modelParams = new ModelParameters(
                                dimF: expectedFreq,
                                dimT: newDimT,
                                nFft: _options.NFft
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not auto-detect model dimensions: {ex.Message}");
            }
        }

        /// <summary>
        /// Log the active execution providers for debugging
        /// </summary>
        private void LogExecutionProviders(InferenceSession session)
        {
            try
            {
                Log.Info("📊 Active ONNX Runtime Configuration:");
                Log.Info($"   ONNX Runtime initialized successfully");
                Log.Info("   To verify GPU/Neural Engine usage:");
#if MACOS
                Log.Info("   - Open Activity Monitor → Window → GPU History");
                Log.Info("   - Check for 'Neural Engine' or 'GPU' activity during processing");
                Log.Info("   - Or run: sudo powermetrics --samplers gpu_power -i 500");
#else
                Log.Info("   - Open Task Manager → Performance → GPU");
                Log.Info("   - Check GPU usage during processing");
#endif
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not log execution providers: {ex.Message}");
            }
        }

        /// <summary>
        /// Pre-calculate Hanning window for STFT/ISTFT optimization
        /// </summary>
        private void PreCalculateHanningWindow()
        {
            _hanningWindow = new float[_modelParams.NFft];
            for (int i = 0; i < _modelParams.NFft; i++)
            {
                _hanningWindow[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / _modelParams.NFft)));
            }
        }

        #endregion
    }
}
