using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class IsoAcquisitionViewModel : ObservableObject
{
    private readonly IIsoWorkflow _acquisition;
    private readonly ILogger<IsoAcquisitionViewModel> _logger;

    //   Observable state                           

    [ObservableProperty]
    private IsoAcquisitionPhase _phase = IsoAcquisitionPhase.Downloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long? _bytesTotal;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private IsoAcquisitionResult? _result;

    
    [ObservableProperty] private DistroManifest? _distro;

    //   Derived                                

    public double ProgressPercent =>
        BytesTotal is > 0 ? BytesCompleted * 100.0 / BytesTotal.Value : 0;

    public bool IsProgressIndeterminate =>
        BytesTotal is null or 0;

    public string PhaseDisplay => Phase switch
    {
        IsoAcquisitionPhase.ResolvingMirror => "Resolving mirror…",
        IsoAcquisitionPhase.Downloading => "Downloading…",
        IsoAcquisitionPhase.VerifyingSha256 => "Verifying SHA-256…",
        IsoAcquisitionPhase.VerifyingGpg => "Verifying GPG signature…",
        IsoAcquisitionPhase.Complete => "Complete",
        _ => string.Empty,
    };

    //   Constructor                              

    public IsoAcquisitionViewModel(
        IIsoWorkflow acquisition,
        ILogger<IsoAcquisitionViewModel> logger)
    {
        _acquisition = acquisition;
        _logger = logger;
    }

    //   API called by MainWindowViewModel                   

    public void Prepare(DistroManifest distro)
    {
        ArgumentNullException.ThrowIfNull(distro);

        Distro = distro;

        IsComplete = false;
        HasError = false;
        ErrorMessage = null;
        Result = null;
        BytesCompleted = 0;
        BytesTotal = null;
        Phase = IsoAcquisitionPhase.Downloading;
    }

    //   Command                                

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AcquireAsync(CancellationToken ct)
    {
        if (Distro is null)
            return;

        IsRunning = true;
        HasError = false;
        ErrorMessage = null;

        var uiProgress = new Progress<IsoAcquisitionProgress>(p =>
        {
            Phase = p.Phase;
            BytesCompleted = p.BytesCompleted;
            BytesTotal = p.BytesTotal;
            OnPropertyChanged(nameof(PhaseDisplay));
        });

        // throttle before the UI thread sees it. Phase changes and the final
        // byte always pass through so the bar never sticks below 100%.
        var progress = new Igloo.Core.Services.ThrottledProgress<IsoAcquisitionProgress>(
            uiProgress,
            forceWhen: (cur, prev) => prev is null
                || cur.Phase != prev.Phase
                || (cur.BytesTotal is { } total && cur.BytesCompleted >= total));

        try
        {
            Result = await _acquisition.AcquireAsync(Distro, progress, ct);
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ISO acquisition cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ISO acquisition failed");
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    //   Property-change hooks                         ─

    partial void OnPhaseChanged(IsoAcquisitionPhase value) =>
        OnPropertyChanged(nameof(PhaseDisplay));

    partial void OnIsCompleteChanged(bool value) =>
        OnPropertyChanged(nameof(PhaseDisplay));
}
