using Logger;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class HTDemucsAudioSeparator
    {
        #region Private Methods - Initialization

        /// <summary>
        /// Auto-detect model parameters from ONNX metadata
        /// </summary>
        private void AutoDetectModelParameters()
        {
            if (_onnxSession == null) return;

            try
            {
                var inputMetadata = _onnxSession.InputMetadata.FirstOrDefault();
                var outputMetadata = _onnxSession.OutputMetadata.FirstOrDefault();

                if (inputMetadata.Value != null)
                {
                    var inputShape = inputMetadata.Value.Dimensions;
                    Log.Info($"Model input shape: [{string.Join(", ", inputShape)}]");

                    // Expected input: [batch, channels, samples]
                    if (inputShape.Length >= 3)
                    {
                        _modelChannels = (int)inputShape[1];
                        if (inputShape[2] > 0)
                        {
                            _modelSegmentLength = (int)inputShape[2];
                        }
                    }
                }

                if (outputMetadata.Value != null)
                {
                    var outputShape = outputMetadata.Value.Dimensions;
                    Log.Info($"Model output shape: [{string.Join(", ", outputShape)}]");

                    // Expected output: [batch, stems, channels, samples]
                    if (outputShape.Length >= 4)
                    {
                        _modelStemCount = (int)outputShape[1];
                    }
                }

                // Override with user settings if provided
                if (_options.SegmentLength > 0)
                {
                    _modelSegmentLength = _options.SegmentLength;
                }

                // Default segment length if not detected
                if (_modelSegmentLength == 0)
                {
                    _modelSegmentLength = _options.ChunkSizeSeconds * _options.TargetSampleRate;
                    Log.Info($"Using default segment length: {_modelSegmentLength} samples");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not auto-detect model parameters: {ex.Message}");
                _modelSegmentLength = _options.ChunkSizeSeconds * _options.TargetSampleRate;
            }
        }

        #endregion
    }
}
