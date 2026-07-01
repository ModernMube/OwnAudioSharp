namespace OwnaudioNET.RustNext.Features.Vocalremover
{
    public partial class MultiModelAudioSeparator
    {
        #region Private Methods - Model Inference

        /// <summary>
        /// Run model inference - AOT-compatible via OrtRunner / OrtTensor
        /// </summary>
        private OrtTensor RunModelInference(OrtTensor stftTensor, ModelSession modelSession)
        {
            if (!modelSession.Info.DisableNoiseReduction)
            {
                // Denoise: run model with normal + negated input, then average
                var stftTensorNeg = new OrtTensor(stftTensor.Shape);
                for (int idx = 0; idx < stftTensor.Length; idx++)
                {
                    stftTensorNeg.SetValue(idx, -stftTensor.GetValue(idx));
                }

                var specPred    = OrtRunner.Run(modelSession.Session, stftTensor);
                var specPredNeg = OrtRunner.Run(modelSession.Session, stftTensorNeg);

                var result = new OrtTensor(specPred.Shape);

                for (int b = 0; b < specPred.Shape[0]; b++)
                    for (int c = 0; c < specPred.Shape[1]; c++)
                        for (int f = 0; f < specPred.Shape[2]; f++)
                            for (int t = 0; t < specPred.Shape[3]; t++)
                            {
                                result[b, c, f, t] =
                                    -specPredNeg[b, c, f, t] * 0.5f + specPred[b, c, f, t] * 0.5f;
                            }

                return result;
            }
            else
            {
                return OrtRunner.Run(modelSession.Session, stftTensor);
            }
        }

        #endregion
    }
}
