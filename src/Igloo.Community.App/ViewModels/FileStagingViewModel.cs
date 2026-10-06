using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Services;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class FileStagingViewModel : ObservableObject
{
    private readonly IMigrationStagingWorkflow _coordinator;
    private readonly ILogger<FileStagingViewModel> _logger;

    private FileStagingRequest? _request;
    private string? _distroId;
    private PreflightReport? _preflightReport;
    private MigrationSetupViewModel? _setup;
    private DiskInfo? _targetDisk;
    private DiskInstallMode _installMode = DiskInstallMode.ReplaceDisk;
    private int _linuxSizeGb;

    //   Observable state                           

    [ObservableProperty] private FileStagingPhase _phase = FileStagingPhase.Scanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesCopied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesTotal;

    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private FileStagingResult? _result;

    //   Derived                                

    public double ProgressPercent => BytesTotal > 0 ? BytesCopied * 100.0 / BytesTotal : 0;
    public bool IsProgressIndeterminate => BytesTotal == 0;

    public string PhaseDisplay => Phase switch
    {
        FileStagingPhase.Scanning => "Scanning files…",
        FileStagingPhase.Copying => "Copying files…",
        FileStagingPhase.Generating => "Generating installer configuration…",
        FileStagingPhase.Complete => "Complete",
        _ => string.Empty,
    };

    //   Constructor                              

    public FileStagingViewModel(
        IMigrationStagingWorkflow coordinator,
        ILogger<FileStagingViewModel> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    //   API called by MainWindowViewModel                   

    public void Prepare(
        MigrationSetupViewModel setup,
        PreflightReport report,
        DistroManifest distro,
        DiskInfo? targetDisk = null,
        DiskInstallMode installMode = DiskInstallMode.ReplaceDisk,
        int linuxSizeGb = 0)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(distro);

        _setup = setup;
        _preflightReport = report;
        _distroId = distro.Id;
        _targetDisk = targetDisk;
        _installMode = installMode;
        _linuxSizeGb = linuxSizeGb;
        _request = new FileStagingRequest(distro.Id, setup.GetSelectedFolderPaths());

        IsComplete = false;
        HasError = false;
        ErrorMessage = null;
        Result = null;
        BytesCopied = 0;
        BytesTotal = 0;
        Phase = FileStagingPhase.Scanning;
        CurrentFile = null;
    }

    //   Command                                

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task StageAsync(CancellationToken ct)
    {
        if (_request is null || _setup is null || _preflightReport is null || _distroId is null)
            return;

        IsRunning = true;
        HasError = false;
        ErrorMessage = null;

        var progress = new Progress<FileStagingProgress>(p =>
        {
            Phase = p.Phase;
            BytesCopied = p.BytesCopied;
            BytesTotal = p.BytesTotal;
            CurrentFile = p.CurrentItem;
            OnPropertyChanged(nameof(PhaseDisplay));
        });

        try
        {
            var userSetup = new UserSetup
            {
                WindowsUsername = _setup.WindowsUsername,
                WindowsComputerName = _setup.WindowsComputerName,
                LinuxUsername = _setup.LinuxUsername,
                LinuxPassword = _setup.LinuxPassword,
                Locale = _setup.Locale,
                Timezone = _setup.Timezone,
                Keymap = _setup.Keymap,
                SelectedFolderNames = _setup.GetSelectedFolderNames(),
                SelectedFolders = _setup.GetSelectedFolders(),
                SelectedBrowserNames = _setup.GetSelectedBrowserNames(),
                SelectedBrowsers = _setup.GetSelectedBrowsers(),
                SuggestedPackages = _setup.GetSelectedSuggestions(),
            };
            Result = await _coordinator.StageAsync(new LocalMigrationStagingInput(_request,
                userSetup, _preflightReport, _targetDisk, _installMode, _linuxSizeGb), progress, ct);
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File staging cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "File staging failed");
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    //   Property-change hooks                         ─

    partial void OnPhaseChanged(FileStagingPhase value) =>
        OnPropertyChanged(nameof(PhaseDisplay));
}
