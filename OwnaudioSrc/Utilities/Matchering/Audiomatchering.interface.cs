using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Utilities.Matchering
{
    /// <summary>
    /// Audio processing status enumeration for UI state management
    /// </summary>
    public enum AudioProcessingStatus
    {
        Idle,
        LoadingAudio,
        AnalyzingSource,
        AnalyzingTarget,
        CalculatingEQ,
        ProcessingAudio,
        WritingOutput,
        Completed,
        Error
    }

    /// <summary>
    /// Progress information for UI updates
    /// </summary>
    public class ProgressEventArgs : EventArgs
    {
        public int ProgressPercentage { get; set; }
        public string CurrentOperation { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public AudioProcessingStatus Status { get; set; }
    }

    /// <summary>
    /// Status change event arguments
    /// </summary>
    public class StatusChangedEventArgs : EventArgs
    {
        public AudioProcessingStatus OldStatus { get; set; }
        public AudioProcessingStatus NewStatus { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Error event arguments
    /// </summary>
    public class ErrorEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
        public string Message { get; set; } = string.Empty;
        public AudioProcessingStatus StatusWhenErrorOccurred { get; set; }
    }

    /// <summary>
    /// Audio analysis completion event arguments
    /// </summary>
    public class AnalysisCompletedEventArgs : EventArgs
    {
        public AudioSpectrum SourceSpectrum { get; set; }
        public AudioSpectrum TargetSpectrum { get; set; }
        public float[] EQAdjustments { get; set; }
        public string OutputFilePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Enhanced AudioAnalyzer with UI integration capabilities
    /// </summary>
    public partial class AudioAnalyzer
    {
        #region Events

        /// <summary>
        /// Raised when processing status changes
        /// </summary>
        public event EventHandler<StatusChangedEventArgs>? StatusChanged;

        /// <summary>
        /// Raised to report progress during operations
        /// </summary>
        public event EventHandler<ProgressEventArgs>? ProgressChanged;

        /// <summary>
        /// Raised when an error occurs during processing
        /// </summary>
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        /// <summary>
        /// Raised when analysis is completed successfully
        /// </summary>
        public event EventHandler<AnalysisCompletedEventArgs>? AnalysisCompleted;

        /// <summary>
        /// Raised when a processing operation is completed
        /// </summary>
        public event EventHandler<EventArgs>? ProcessingCompleted;

        #endregion

        #region Properties

        /// <summary>
        /// Current processing status
        /// </summary>
        public AudioProcessingStatus CurrentStatus { get; private set; } = AudioProcessingStatus.Idle;

        /// <summary>
        /// Indicates if the analyzer is currently busy
        /// </summary>
        public bool IsBusy => CurrentStatus != AudioProcessingStatus.Idle &&
                             CurrentStatus != AudioProcessingStatus.Completed &&
                             CurrentStatus != AudioProcessingStatus.Error;

        /// <summary>
        /// Current operation progress (0-100)
        /// </summary>
        public int CurrentProgress { get; private set; }

        /// <summary>
        /// Cancellation token source for operation cancellation
        /// </summary>
        private CancellationTokenSource? _cancellationTokenSource;

        #endregion

        #region Public Async Methods

        /// <summary>
        /// Asynchronously analyzes audio file with progress reporting
        /// </summary>
        /// <param name="filePath">Path to audio file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Audio spectrum analysis result</returns>
        public async Task<AudioSpectrum> AnalyzeAudioFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (IsBusy)
                throw new InvalidOperationException("Analyzer is currently busy with another operation");

            try
            {
                UpdateStatus(AudioProcessingStatus.LoadingAudio, "Loading audio file...");

                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    UpdateStatus(AudioProcessingStatus.AnalyzingSource, "Analyzing audio spectrum...");
                    ReportProgress(0, "Starting analysis", "Preparing audio data");

                    var result = AnalyzeAudioFile(filePath);

                    cancellationToken.ThrowIfCancellationRequested();
                    ReportProgress(100, "Analysis completed", $"Processed {filePath}");

                    return result;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                UpdateStatus(AudioProcessingStatus.Idle, "Operation cancelled");
                throw;
            }
            catch (Exception ex)
            {
                HandleError(ex, "Error during audio analysis");
                throw;
            }
        }

        /// <summary>
        /// Asynchronously processes EQ matching with full progress reporting
        /// </summary>
        /// <param name="sourceFile">Source audio file path</param>
        /// <param name="targetFile">Target audio file path</param>
        /// <param name="outputFile">Output file path</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async operation</returns>
        public async Task ProcessEQMatchingAsync(string sourceFile, string targetFile, string outputFile,
            CancellationToken cancellationToken = default)
        {
            if (IsBusy)
                throw new InvalidOperationException("Analyzer is currently busy with another operation");

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                UpdateStatus(AudioProcessingStatus.AnalyzingSource, "Starting EQ matching process...");

                await Task.Run(async () =>
                {
                    // Step 1: Analyze source
                    ReportProgress(10, "Analyzing source audio", sourceFile);
                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    var sourceSpectrum = await AnalyzeAudioFileWithProgressAsync(sourceFile, 10, 30, _cancellationTokenSource.Token);

                    // Step 2: Analyze target
                    UpdateStatus(AudioProcessingStatus.AnalyzingTarget, "Analyzing target audio...");
                    ReportProgress(30, "Analyzing target audio", targetFile);
                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    var targetSpectrum = await AnalyzeAudioFileWithProgressAsync(targetFile, 30, 50, _cancellationTokenSource.Token);

                    // Step 3: Calculate EQ adjustments
                    UpdateStatus(AudioProcessingStatus.CalculatingEQ, "Calculating EQ adjustments...");
                    ReportProgress(50, "Calculating EQ adjustments", "Computing optimal frequency response");
                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    var eqAdjustments = await Task.Run(() => CalculateDirectEQAdjustments(sourceSpectrum, targetSpectrum), _cancellationTokenSource.Token);
                    var ampSettings = await Task.Run(() => CalculateDynamicAmpSettings(sourceSpectrum, targetSpectrum), _cancellationTokenSource.Token);

                    ReportProgress(70, "EQ calculations completed", "Applying audio processing...");

                    // Step 4: Process audio
                    UpdateStatus(AudioProcessingStatus.ProcessingAudio, "Processing audio with EQ...");
                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    await ApplyDirectEQProcessingAsync(sourceFile, outputFile, eqAdjustments, ampSettings,
                        sourceSpectrum, targetSpectrum, 70, 95, _cancellationTokenSource.Token);

                    // Step 5: Write output
                    UpdateStatus(AudioProcessingStatus.WritingOutput, "Writing output file...");
                    ReportProgress(95, "Writing output file", outputFile);
                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    ReportProgress(100, "Processing completed", "EQ matching finished successfully");

                    // Raise completion event
                    AnalysisCompleted?.Invoke(this, new AnalysisCompletedEventArgs
                    {
                        SourceSpectrum = sourceSpectrum,
                        TargetSpectrum = targetSpectrum,
                        EQAdjustments = eqAdjustments,
                        OutputFilePath = outputFile
                    });

                }, _cancellationTokenSource.Token);

                UpdateStatus(AudioProcessingStatus.Completed, "EQ matching completed successfully");
                ProcessingCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                UpdateStatus(AudioProcessingStatus.Idle, "Operation cancelled by user");
                throw;
            }
            catch (Exception ex)
            {
                HandleError(ex, "Error during EQ matching process");
                throw;
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        /// <summary>
        /// Asynchronously processes enhanced preset with progress reporting
        /// </summary>
        /// <param name="sourceFile">Source file path</param>
        /// <param name="outputFile">Output file path</param>
        /// <param name="system">Playback system preset</param>
        /// <param name="tempDirectory">Temporary directory</param>
        /// <param name="eqOnlyMode">EQ only mode flag</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async operation</returns>
        public async Task ProcessWithEnhancedPresetAsync(string sourceFile, string outputFile,
            PlaybackSystem system, string tempDirectory = null, bool eqOnlyMode = true,
            CancellationToken cancellationToken = default)
        {
            if (IsBusy)
                throw new InvalidOperationException("Analyzer is currently busy with another operation");

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                UpdateStatus(AudioProcessingStatus.LoadingAudio, "Starting enhanced preset processing...");

                await Task.Run(() =>
                {
                    ProcessWithEnhancedPreset(sourceFile, outputFile, system, tempDirectory, eqOnlyMode);
                }, _cancellationTokenSource.Token);

                UpdateStatus(AudioProcessingStatus.Completed, "Enhanced preset processing completed");
                ProcessingCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                UpdateStatus(AudioProcessingStatus.Idle, "Operation cancelled by user");
                throw;
            }
            catch (Exception ex)
            {
                HandleError(ex, "Error during enhanced preset processing");
                throw;
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        /// <summary>
        /// Cancels the current operation if possible
        /// </summary>
        public void CancelCurrentOperation()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                UpdateStatus(AudioProcessingStatus.Idle, "Cancelling operation...");
            }
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Updates the current status and raises StatusChanged event
        /// </summary>
        /// <param name="newStatus">New status</param>
        /// <param name="message">Status message</param>
        private void UpdateStatus(AudioProcessingStatus newStatus, string message = "")
        {
            var oldStatus = CurrentStatus;
            CurrentStatus = newStatus;

            StatusChanged?.Invoke(this, new StatusChangedEventArgs
            {
                OldStatus = oldStatus,
                NewStatus = newStatus,
                Message = message
            });
        }

        /// <summary>
        /// Reports progress and raises ProgressChanged event
        /// </summary>
        /// <param name="percentage">Progress percentage (0-100)</param>
        /// <param name="operation">Current operation description</param>
        /// <param name="details">Additional details</param>
        private void ReportProgress(int percentage, string operation, string details = "")
        {
            CurrentProgress = Math.Max(0, Math.Min(100, percentage));

            ProgressChanged?.Invoke(this, new ProgressEventArgs
            {
                ProgressPercentage = CurrentProgress,
                CurrentOperation = operation,
                Details = details,
                Status = CurrentStatus
            });
        }

        /// <summary>
        /// Handles errors and raises ErrorOccurred event
        /// </summary>
        /// <param name="exception">Exception that occurred</param>
        /// <param name="message">Error message</param>
        private void HandleError(Exception exception, string message)
        {
            var statusWhenError = CurrentStatus;
            UpdateStatus(AudioProcessingStatus.Error, message);

            ErrorOccurred?.Invoke(this, new ErrorEventArgs
            {
                Exception = exception,
                Message = message,
                StatusWhenErrorOccurred = statusWhenError
            });
        }

        /// <summary>
        /// Analyzes audio file with progress reporting within a range
        /// </summary>
        /// <param name="filePath">Audio file path</param>
        /// <param name="startProgress">Starting progress percentage</param>
        /// <param name="endProgress">Ending progress percentage</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Audio spectrum analysis result</returns>
        private async Task<AudioSpectrum> AnalyzeAudioFileWithProgressAsync(string filePath,
            int startProgress, int endProgress, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Simulate progress during analysis
                for (int i = 0; i <= 10; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int currentProgress = startProgress + (endProgress - startProgress) * i / 10;
                    ReportProgress(currentProgress, $"Analyzing {System.IO.Path.GetFileName(filePath)}",
                        $"Processing segment {i + 1}/10");

                    if (i < 10) // Don't sleep on the last iteration
                        Task.Delay(100, cancellationToken).Wait(cancellationToken);
                }

                return AnalyzeAudioFile(filePath);
            }, cancellationToken);
        }

        /// <summary>
        /// Applies direct EQ processing with progress reporting
        /// </summary>
        /// <param name="inputFile">Input file path</param>
        /// <param name="outputFile">Output file path</param>
        /// <param name="eqAdjustments">EQ adjustments</param>
        /// <param name="dynamicAmp">Dynamic amplifier settings</param>
        /// <param name="sourceSpectrum">Source spectrum</param>
        /// <param name="targetSpectrum">Target spectrum</param>
        /// <param name="startProgress">Starting progress percentage</param>
        /// <param name="endProgress">Ending progress percentage</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async operation</returns>
        private async Task ApplyDirectEQProcessingAsync(string inputFile, string outputFile,
            float[] eqAdjustments, DynamicAmpSettings dynamicAmp,
            AudioSpectrum sourceSpectrum, AudioSpectrum targetSpectrum,
            int startProgress, int endProgress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // This would be integrated into the existing ApplyDirectEQProcessing method
                // to report progress during chunk processing
                ApplyDirectEQProcessing(inputFile, outputFile, eqAdjustments, dynamicAmp, sourceSpectrum, targetSpectrum);

                // For now, simulate progress
                for (int i = 0; i <= 10; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int currentProgress = startProgress + (endProgress - startProgress) * i / 10;
                    ReportProgress(currentProgress, "Processing audio", $"Chunk {i + 1}/10");

                    if (i < 10)
                        Task.Delay(200, cancellationToken).Wait(cancellationToken);
                }
            }, cancellationToken);
        }

        #endregion
    }
}
