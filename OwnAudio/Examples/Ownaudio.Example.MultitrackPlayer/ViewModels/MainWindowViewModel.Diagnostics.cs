using CommunityToolkit.Mvvm.ComponentModel;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Diagnostics and monitoring functionality for MainWindowViewModel.
/// Contains: Dropout tracking and performance monitoring.
/// </summary>
public partial class MainWindowViewModel
{
    #region Properties

    /// <summary>
    /// Gets or sets the total dropout count (NEW - v2.4.0+).
    /// </summary>
    [ObservableProperty]
    private int _dropoutCount;

    /// <summary>
    /// Gets or sets the last dropout message (NEW - v2.4.0+).
    /// </summary>
    [ObservableProperty]
    private string? _lastDropoutMessage;

    #endregion
}
