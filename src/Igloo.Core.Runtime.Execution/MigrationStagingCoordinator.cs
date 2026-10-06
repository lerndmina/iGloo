using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Services;
using Igloo.Migration;
using Igloo.Migration.Chromium;
using Igloo.Preflight;
using Microsoft.Extensions.Logging;

namespace Igloo.Core.Runtime.Execution;

/// <summary>Local in-process staging workflow. Sensitive input never enters the public JSON contract.</summary>
public sealed record MigrationStagingInput(FileStagingRequest Files, UserSetup User,
    PreflightReport ObservedMachine, DiskInfo? TargetDisk, DiskInstallMode InstallMode,
    int LinuxSizeGb);

internal sealed record MigrationStagingHooks(
    Func<CancellationToken, Task<IReadOnlyList<WifiNetwork>>> ScanWifi,
    Func<IReadOnlyList<BrowserMigration>, string?, CancellationToken, Task<IReadOnlyList<BrowserMigration>>> AttachBrowsers,
    Func<MigrationManifest, string, MigrationManifest> AttachWallpaper,
    Func<MigrationManifest, string, MigrationManifest> AttachAccountPicture);

public sealed class MigrationStagingCoordinator
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private readonly IFileStagingService _staging;
    private readonly DistroRegistry _registry;
    private readonly ILogger<MigrationStagingCoordinator> _logger;
    private readonly MigrationStagingHooks _hooks;

    public MigrationStagingCoordinator(IFileStagingService staging, DistroRegistry registry,
        ILogger<MigrationStagingCoordinator> logger) : this(staging, registry, logger, null) { }

    internal MigrationStagingCoordinator(IFileStagingService staging, DistroRegistry registry,
        ILogger<MigrationStagingCoordinator> logger, MigrationStagingHooks? hooks)
    {
        _staging = staging;
        _registry = registry;
        _logger = logger;
        _hooks = hooks ?? new(
            ct => Task.Run<IReadOnlyList<WifiNetwork>>(WindowsWifiScanner.Scan, ct),
            (browsers, password, ct) => Task.Run(() =>
                BrowserCredentialMigration.AttachCredentials(browsers, password, logger), ct),
            AttachWallpaper,
            AttachAccountPicture);
    }

    public async Task<FileStagingResult> StageAsync(MigrationStagingInput input,
        IProgress<FileStagingProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Files);
        ArgumentNullException.ThrowIfNull(input.User);
        ArgumentNullException.ThrowIfNull(input.ObservedMachine);

        var stagingResult = await _staging.StageAsync(input.Files, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new(FileStagingPhase.Generating, stagingResult.TotalBytesCopied,
            stagingResult.TotalBytesCopied, string.Empty));

        var wifiNetworks = await _hooks.ScanWifi(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Detected {Count} saved Wi-Fi network(s) for migration", wifiNetworks.Count);
        var selectedBrowsers = await _hooks.AttachBrowsers(input.User.SelectedBrowsers,
            input.User.LinuxPassword, cancellationToken).ConfigureAwait(false);
        var user = input.User with { SelectedBrowsers = selectedBrowsers, WifiNetworks = wifiNetworks };
        var manifest = ManifestGeneratorService.Generate(input.Files.DistroId, user,
            input.ObservedMachine, stagingResult, input.TargetDisk, input.InstallMode, input.LinuxSizeGb);
        manifest = await Task.Run(() => _hooks.AttachWallpaper(manifest, stagingResult.StagingDirectory),
            cancellationToken).ConfigureAwait(false);
        manifest = await Task.Run(() => _hooks.AttachAccountPicture(manifest, stagingResult.StagingDirectory),
            cancellationToken).ConfigureAwait(false);

        var manifestPath = Path.Join(stagingResult.StagingDirectory, "migration-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, PrettyJson),
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Migration manifest written to {Path}", manifestPath);
        if (_registry.TryGet(input.Files.DistroId, out var plugin))
            await PluginArtifactWriter.WriteAsync(plugin, manifest, stagingResult.StagingDirectory,
                _logger, cancellationToken).ConfigureAwait(false);
        else
            _logger.LogWarning("No plugin found for distro '{Id}' - installer config not generated.",
                input.Files.DistroId);
        return stagingResult;
    }

    private MigrationManifest AttachWallpaper(MigrationManifest manifest, string stagingDirectory)
    {
        try
        {
            var source = WallpaperReader.TryFindWallpaper(_logger);
            if (source is null) return manifest;
            var ext = Path.GetExtension(source);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var fileName = $"igloo-wallpaper{ext}";
            File.Copy(source, Path.Join(stagingDirectory, fileName), overwrite: true);
            _logger.LogInformation("Wallpaper staged from {Source} as {FileName}", source, fileName);
            return manifest with { Wallpaper = new WallpaperMigration { FileName = fileName, OriginalPath = source } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not stage the wallpaper - skipping (non-fatal)");
            return manifest;
        }
    }

    private MigrationManifest AttachAccountPicture(MigrationManifest manifest, string stagingDirectory)
    {
        try
        {
            var source = AccountPictureReader.TryFindAccountPicture(_logger);
            if (source is null) return manifest;
            const string fileName = "igloo-avatar.jpg";
            File.Copy(source, Path.Join(stagingDirectory, fileName), overwrite: true);
            _logger.LogInformation("Account picture staged from {Source}", source);
            return manifest with { AccountPicture = new AccountPictureMigration
            {
                FileName = fileName,
                OriginalPath = source,
            } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not stage the account picture - skipping (non-fatal)");
            return manifest;
        }
    }
}
