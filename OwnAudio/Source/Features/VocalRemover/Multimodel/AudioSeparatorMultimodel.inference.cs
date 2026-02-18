using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class MultiModelAudioSeparator
    {
        #region Private Methods - Model Inference

        /// <summary>
        /// Run model inference
        /// </summary>
        private Tensor<float> RunModelInference(DenseTensor<float> stftTensor, ModelSession modelSession)
        {
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", stftTensor) };

            if (!modelSession.Info.DisableNoiseReduction)
            {
                // Denoise logic: run model with normal + negated input, then average
                var stftTensorNeg = new DenseTensor<float>(stftTensor.Dimensions);
                for (int idx = 0; idx < stftTensor.Length; idx++)
                {
                    stftTensorNeg.SetValue(idx, -stftTensor.GetValue(idx));
                }
                var inputsNeg = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", stftTensorNeg) };

                using var outputs = modelSession.Session.Run(inputs);
                using var outputsNeg = modelSession.Session.Run(inputsNeg);

                var specPred = outputs.First().AsTensor<float>();
                var specPredNeg = outputsNeg.First().AsTensor<float>();

                var result = new DenseTensor<float>(specPred.Dimensions);

                for (int b = 0; b < specPred.Dimensions[0]; b++)
                    for (int c = 0; c < specPred.Dimensions[1]; c++)
                        for (int f = 0; f < specPred.Dimensions[2]; f++)
                            for (int t = 0; t < specPred.Dimensions[3]; t++)
                            {
                                float val = -specPredNeg[b, c, f, t] * 0.5f + specPred[b, c, f, t] * 0.5f;
                                ((DenseTensor<float>)result)[b, c, f, t] = val;
                            }

                return result;
            }
            else
            {
                using var outputs = modelSession.Session.Run(inputs);
                var result = outputs.First().AsTensor<float>();

                var resultCopy = new DenseTensor<float>(result.Dimensions);
                for (int i = 0; i < result.Length; i++)
                {
                    resultCopy.SetValue(i, result.GetValue(i));
                }
                return resultCopy;
            }
        }

        #endregion
    }
}
