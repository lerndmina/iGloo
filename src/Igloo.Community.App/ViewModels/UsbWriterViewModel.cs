using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class UsbWriterViewModel : ObservableObject
{
    private readonly IUsbPreparation _preparation;
    private readonly ILogger<UsbWriterViewModel> _logger;

    private string? _isoPath;
    private string? _stagingDirectory;
    private DiskInfo? _targetDisk;
    private DiskInstallMode _installMode = DiskInstallMode.ReplaceDisk;
    private int _linuxSizeGb;

    //   Observable state                            

    [ObservableProperty]
    private ObservableCollection<UsbDriveInfo> _drives = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    private UsbDriveInfo? _selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    private bool _isEnumerating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyPropertyChangedFor(nameof(IsCancelable))]
    private bool _isRunning;

    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGrubPatchNote))]
    private string? _grubPatchNote;

    public bool HasGrubPatchNote => !string.IsNullOrEmpty(GrubPatchNote);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorDetail))]
    private string? _errorDetail;

    public bool HasErrorDetail => !string.IsNullOrEmpty(ErrorDetail);

    [ObservableProperty] private string? _phaseDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCancelable))]
    private UsbWritePhase _currentPhase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesWritten;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private long _bytesTotal;

    //   Derived                                ─

    public bool CanWrite => SelectedDrive is not null && !IsRunning && !IsEnumerating && !IsComplete;

    public bool IsCancelable => !IsRunning || CurrentPhase != UsbWritePhase.CreatingOemdrv;

    public double ProgressPercent => BytesTotal > 0 ? BytesWritten * 100.0 / BytesTotal : 0;
    public bool IsProgressIndeterminate => BytesTotal == 0;

    //   Constructor                              ─

    public UsbWriterViewModel(
        IUsbPreparation preparation,
        ILogger<UsbWriterViewModel> logger)
    {
        _preparation = preparation;
        _logger = logger;
    }

    //   API called by MainWindowViewModel                   ─

    public void Prepare(
        IsoAcquisitionResult isoResult,
        FileStagingResult stagingResult,
        DiskInfo? targetDisk = null,
        DiskInstallMode installMode = DiskInstallMode.ReplaceDisk,
        int linuxSizeGb = 0)
    {
        ArgumentNullException.ThrowIfNull(isoResult);
        ArgumentNullException.ThrowIfNull(stagingResult);

        _isoPath = isoResult.LocalPath;
        _stagingDirectory = stagingResult.StagingDirectory;
        _targetDisk = targetDisk;
        _installMode = installMode;
        _linuxSizeGb = linuxSizeGb;

        IsComplete = false;
        HasError = false;
        ErrorMessage = null;
        ErrorDetail = null;
        PhaseDisplay = null;
        BytesWritten = 0;
        BytesTotal = 0;
        IsRunning = false;
        CurrentPhase = UsbWritePhase.WritingIso;
        GrubPatchNote = null;
    }

    //   Commands                                

    
    [RelayCommand]
    private async Task RefreshDrivesAsync(CancellationToken ct)
    {
        IsEnumerating = true;
        try
        {
            var found = await _preparation.EnumerateDrivesAsync(ct);
            Drives.Clear();
            foreach (var d in found)
                Drives.Add(d);

            if (Drives.Count > 0)
                SelectedDrive = Drives[0];

            _logger.LogInformation("Drive enumeration complete: {Count} USB drive(s) found", Drives.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("USB drive enumeration cancelled (navigated away)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to enumerate USB drives");
        }
        finally
        {
            IsEnumerating = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanWrite))]
    private async Task WriteAsync(CancellationToken ct)
    {
        if (SelectedDrive is null || _isoPath is null || _stagingDirectory is null)
            return;

        IsRunning = true;
        HasError = false;
        ErrorMessage = null;
        ErrorDetail = null;
        BytesWritten = 0;
        BytesTotal = 0;
        PhaseDisplay = "Preparing…";

        var progress = new Progress<UsbWriteProgress>(p =>
        {
            BytesWritten = p.BytesWritten;
            BytesTotal = p.BytesTotal;
            CurrentPhase = p.Phase;    // drives IsCancelable and cancel-message logic
            PhaseDisplay = p.Phase switch
            {
                UsbWritePhase.ShrinkingPartition => "Shrinking Windows partition…",
                UsbWritePhase.WritingIso => "Writing installer image…",
                UsbWritePhase.CreatingOemdrv => "Creating OEMDRV partition…",
                UsbWritePhase.PatchingGrub => "Patching GRUB configuration…",
                UsbWritePhase.CopyingFiles => "Copying migration files…",
                UsbWritePhase.Complete => "Complete",
                _ => string.Empty,
            };

            // Capture the last GRUB patch message so it is displayed on completion.
            if (p.Phase == UsbWritePhase.PatchingGrub && !string.IsNullOrEmpty(p.Message))
                GrubPatchNote = p.Message;
        });

        try
        {
            //   Step 0: Shrink Windows partition (dual-boot only)       ─
            if (_installMode == DiskInstallMode.DualBoot && _targetDisk is not null && _linuxSizeGb > 0)
            {
                CurrentPhase = UsbWritePhase.ShrinkingPartition;
                PhaseDisplay = "Shrinking Windows partition…";

                var shrinkProgress = new Progress<string>(msg =>
                {
                    PhaseDisplay = msg;
                    _logger.LogInformation("[resize] {Message}", msg);
                });

                await _preparation.WriteAsync(new UsbPreparationInput(SelectedDrive, _isoPath,
                    _stagingDirectory, _targetDisk, _installMode, _linuxSizeGb), progress,
                    shrinkProgress, ct);
            }
            else
                await _preparation.WriteAsync(new UsbPreparationInput(SelectedDrive, _isoPath,
                    _stagingDirectory, _targetDisk, _installMode, _linuxSizeGb), progress, null, ct);

            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("USB write cancelled at phase {Phase}", CurrentPhase);

            HasError = true;
            ErrorMessage = CurrentPhase == UsbWritePhase.WritingIso
                // Cancelled mid-ISO: the image on the stick is truncated.
                ? "Write cancelled - the USB drive contains an incomplete installer image " +
                  "and must be re-written before it can be used. " +
                  "Return to this step and run the writer again."
                // Cancelled after Phase 1 (during file copy): ISO is intact, files incomplete.
                : "Write cancelled - the installer image is on the drive but the migration " +
                  "files were not fully copied. " +
                  "Return to this step and run the writer again to complete it.";
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "USB write failed - insufficient rights");
            HasError = true;
            ErrorMessage = ex.Message;
            ErrorDetail = BuildErrorDetail(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "USB write failed");
            HasError = true;
            ErrorMessage = ex.Message;
            ErrorDetail = BuildErrorDetail(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    
    [RelayCommand]
    private static void Finish() => Application.Current.Shutdown();

    //   Error formatting                            

    private static string BuildErrorDetail(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        var depth = 0;

        while (current is not null)
        {
            if (depth > 0)
                sb.AppendLine().AppendLine("  inner exception                ─");

            sb.Append('[').Append(current.GetType().FullName).AppendLine("]");
            sb.AppendLine(current.Message);

            if (current is System.ComponentModel.Win32Exception w32)
                sb.Append("Win32 error code: ").Append(w32.NativeErrorCode)
                  .Append(" (0x").Append(w32.NativeErrorCode.ToString("X8", CultureInfo.InvariantCulture)).AppendLine(")");

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                sb.AppendLine();
                sb.AppendLine(current.StackTrace);
            }

            current = current.InnerException;
            depth++;
        }

        return sb.ToString().TrimEnd();
    }
}
