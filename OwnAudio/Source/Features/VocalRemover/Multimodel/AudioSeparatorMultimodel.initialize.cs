using Microsoft.ML.OnnxRuntime;
using Logger;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class MultiModelAudioSeparator
    {
        #region Inner Classes

        /// <summary>
        /// Holds ONNX session and parameters for a single model
        /// </summary>
        private class ModelSession
        {
            public MultiModelInfo Info { get; set; } = null!;
            public InferenceSession Session { get; set; } = null!;
            public ModelParameters Parameters { get; set; } = null!;
            public ModelOutputType ResolvedOutputType { get; set; } = ModelOutputType.Instrumental;
        }

        #endregion

        #region Private Methods - Initialization

        /// <summary>
        /// Initialize a single model session
        /// </summary>
        private void InitializeModel(MultiModelInfo modelInfo)
        {
            bool useEmbeddedModel = string.IsNullOrEmpty(modelInfo.ModelPath) || !File.Exists(modelInfo.ModelPath);

            if (useEmbeddedModel && modelInfo.Model == InternalModel.None)
            {
                throw new InvalidOperationException(
                    $"Model '{modelInfo.Name}': Either ModelPath must be valid or Model must be set to a valid InternalModel.");
            }

            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };

            // Configure execution providers
            if (_options.EnableGPU)
            {
#if MACOS
                try
                {
                    Log.Info($"{modelInfo.Name}: Attempting to enable CoreML execution provider...");
                    Console.WriteLine($"[DEBUG] {modelInfo.Name}: MACOS symbol is defined. Enabling CoreML...");

                    try
                    {
                        var coremlOptions = new Dictionary<string, string>
                        {
                            ["ModelFormat"] = "MLProgram",
                            ["MLComputeUnits"] = "ALL",
                            ["RequireStaticInputShapes"] = "0"
                        };

                        sessionOptions.AppendExecutionProvider("CoreML", coremlOptions);
                        Log.Info($"{modelInfo.Name}: ✅ CoreML enabled (MLProgram format, ALL compute units).");
                        Console.WriteLine($"[DEBUG] {modelInfo.Name}: ✅ CoreML enabled with MLProgram format (CPU+GPU+ANE).");
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"{modelInfo.Name}:    MLProgram failed, trying NeuralNetwork format...");
                        Console.WriteLine($"[DEBUG] {modelInfo.Name}: MLProgram failed ({ex.Message}). Trying NeuralNetwork format...");

                        try
                        {
                            var coremlOptions = new Dictionary<string, string>
                            {
                                ["ModelFormat"] = "NeuralNetwork",
                                ["MLComputeUnits"] = "ALL"
                            };

                            sessionOptions.AppendExecutionProvider("CoreML", coremlOptions);
                            Log.Info($"{modelInfo.Name}: ✅ CoreML enabled (NeuralNetwork format fallback).");
                            Console.WriteLine($"[DEBUG] {modelInfo.Name}: ✅ CoreML enabled with NeuralNetwork format.");
                        }
                        catch (Exception ex2)
                        {
                            Log.Warning($"{modelInfo.Name}: ❌ Both CoreML formats failed.");
                            Console.WriteLine($"[DEBUG] {modelInfo.Name}: ❌ NeuralNetwork also failed ({ex2.Message}).");
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"{modelInfo.Name}: ❌ Failed to enable CoreML: {ex.Message}");
                    Console.WriteLine($"[DEBUG] {modelInfo.Name}: ❌ Failed to enable CoreML: {ex.Message}");
                    Log.Warning($"{modelInfo.Name}:    Details: {ex.GetType().Name}");
                    if (ex.InnerException != null)
                    {
                        Log.Warning($"{modelInfo.Name}:    Inner: {ex.InnerException.Message}");
                    }
                    Log.Info($"{modelInfo.Name}: ⚠️  Falling back to CPU.");
                    sessionOptions.AppendExecutionProvider_CPU();
                }
#else
                try
                {
                    Console.WriteLine($"[DEBUG] {modelInfo.Name}: MACOS symbol is NOT defined. Using CUDA/CPU...");
                    Log.Info($"{modelInfo.Name}: Attempting to enable CUDA...");
                    sessionOptions.AppendExecutionProvider_CUDA();
                    Log.Info($"{modelInfo.Name}: ✅ CUDA execution provider enabled.");
                }
                catch (Exception ex)
                {
                    Log.Warning($"{modelInfo.Name}: ❌ Failed to enable CUDA: {ex.Message}");
                    Log.Info($"{modelInfo.Name}: ⚠️  Falling back to CPU.");
                    sessionOptions.AppendExecutionProvider_CPU();
                }
#endif
            }
            else
            {
                sessionOptions.AppendExecutionProvider_CPU();
                Log.Info($"{modelInfo.Name}: Using CPU (GPU disabled in options).");
            }

            // Load model
            InferenceSession onnxSession;
            if (useEmbeddedModel)
            {
                Log.Info($"{modelInfo.Name}: Loading embedded model: {modelInfo.Model}");
                var modelBytes = AudioSeparationExtensions.LoadModelBytes(modelInfo.Model);
                onnxSession = new InferenceSession(modelBytes, sessionOptions);
            }
            else
            {
                Log.Info($"{modelInfo.Name}: Loading model from file: {modelInfo.ModelPath}");
                onnxSession = new InferenceSession(modelInfo.ModelPath, sessionOptions);
            }

            var modelParams = new ModelParameters(
                dimF: modelInfo.DimF,
                dimT: modelInfo.DimT,
                nFft: modelInfo.NFft
            );

            AutoDetectModelDimensions(onnxSession, ref modelParams, modelInfo.Name);

            if (!_hanningWindows.ContainsKey(modelParams.NFft))
            {
                _hanningWindows[modelParams.NFft] = PreCalculateHanningWindow(modelParams.NFft);
            }

            ModelOutputType resolvedOutputType = modelInfo.OutputType ?? AutoDetectOutputType(modelInfo, onnxSession);

            _modelSessions.Add(new ModelSession
            {
                Info = modelInfo,
                Session = onnxSession,
                Parameters = modelParams,
                ResolvedOutputType = resolvedOutputType
            });

            Log.Info($"{modelInfo.Name}: Initialized with DimF={modelParams.DimF}, DimT={modelParams.DimT}, NFft={modelParams.NFft}");
            Log.Info($"{modelInfo.Name}: Output type: {resolvedOutputType} {(modelInfo.OutputType.HasValue ? "(explicit)" : "(auto-detected)")}");
        }

        /// <summary>
        /// Auto-detect output type from ONNX metadata and filename
        /// </summary>
        private ModelOutputType AutoDetectOutputType(MultiModelInfo modelInfo, InferenceSession session)
        {
            // Strategy 1: Check ONNX output metadata
            try
            {
                var outputMetadata = session.OutputMetadata;
                foreach (var output in outputMetadata)
                {
                    string outputName = output.Key.ToLowerInvariant();

                    if (outputName.Contains("vocal") || outputName.Contains("voice") || outputName.Contains("singing"))
                    {
                        Log.Info($"{modelInfo.Name}: Auto-detected OutputType = Vocals (based on ONNX output name: '{output.Key}')");
                        return ModelOutputType.Vocals;
                    }

                    if (outputName.Contains("instrumental") || outputName.Contains("instrum") ||
                        outputName.Contains("accomp") || outputName.Contains("karaoke") || outputName.Contains("music"))
                    {
                        Log.Info($"{modelInfo.Name}: Auto-detected OutputType = Instrumental (based on ONNX output name: '{output.Key}')");
                        return ModelOutputType.Instrumental;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"{modelInfo.Name}: Could not read ONNX output metadata for auto-detection: {ex.Message}");
            }

            // Strategy 2: Check model filename or name
            string[] namesToCheck = new[] { modelInfo.Name, modelInfo.ModelPath ?? "", modelInfo.Model.ToString() };

            foreach (var name in namesToCheck)
            {
                if (string.IsNullOrEmpty(name)) continue;

                string nameLower = name.ToLowerInvariant();

                if (nameLower.Contains("vocal") || nameLower.Contains("voice") ||
                    nameLower.Contains("singing") || nameLower.Contains("vox"))
                {
                    Log.Info($"{modelInfo.Name}: Auto-detected OutputType = Vocals (based on model name/path)");
                    return ModelOutputType.Vocals;
                }

                if (nameLower.Contains("instrumental") || nameLower.Contains("instrum") ||
                    nameLower.Contains("accomp") || nameLower.Contains("karaoke") ||
                    nameLower.Contains("music") || nameLower.Contains("backing"))
                {
                    Log.Info($"{modelInfo.Name}: Auto-detected OutputType = Instrumental (based on model name/path)");
                    return ModelOutputType.Instrumental;
                }
            }

            // Strategy 3: Check internal model type
            if (modelInfo.Model != InternalModel.None)
            {
                string modelTypeName = modelInfo.Model.ToString().ToLowerInvariant();

                if (modelTypeName.Contains("vocal") || modelTypeName.Contains("voice"))
                {
                    Log.Info($"{modelInfo.Name}: Auto-detected OutputType = Vocals (based on InternalModel type)");
                    return ModelOutputType.Vocals;
                }
            }

            Log.Info($"{modelInfo.Name}: Auto-detection inconclusive, defaulting to OutputType = Instrumental");
            return ModelOutputType.Instrumental;
        }

        /// <summary>
        /// Auto-detect model dimensions from ONNX metadata
        /// </summary>
        private void AutoDetectModelDimensions(InferenceSession session, ref ModelParameters modelParams, string modelName)
        {
            try
            {
                var inputMetadata = session.InputMetadata;
                if (inputMetadata.ContainsKey("input"))
                {
                    var inputShape = inputMetadata["input"].Dimensions;

                    if (inputShape.Length >= 4)
                    {
                        int expectedFreq = (int)inputShape[2];
                        int expectedTime = (int)inputShape[3];

                        if (expectedFreq != modelParams.DimF || expectedTime != modelParams.DimT)
                        {
                            Log.Info($"{modelName}: Auto-adjusting parameters to match ONNX model...");
                            int newDimT = (int)Math.Log2(expectedTime);

                            modelParams = new ModelParameters(
                                dimF: expectedFreq,
                                dimT: newDimT,
                                nFft: modelParams.NFft
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"{modelName}: Could not auto-detect dimensions: {ex.Message}");
            }
        }

        /// <summary>
        /// Pre-calculate Hanning window for STFT/ISTFT optimization
        /// </summary>
        private float[] PreCalculateHanningWindow(int nFft)
        {
            var window = new float[nFft];
            for (int i = 0; i < nFft; i++)
            {
                window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / nFft)));
            }
            return window;
        }

        #endregion
    }
}
