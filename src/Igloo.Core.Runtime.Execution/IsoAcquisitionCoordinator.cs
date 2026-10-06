using Igloo.Core.Abstractions;
using Igloo.Core.Models;

namespace Igloo.Core.Runtime.Execution;

/// <summary>Shared ISO specification and verification path for Community and the command handler.</summary>
public sealed class IsoAcquisitionCoordinator(IIsoAcquisitionService acquisition)
{
    public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(distro);
        if (!distro.IsAvailable) throw new NotSupportedException("Distribution is not available for installation.");
        return acquisition.AcquireAsync(CreateSpecification(distro), progress, cancellationToken);
    }

    public static IsoSpecification CreateSpecification(DistroManifest distro)
    {
        ArgumentNullException.ThrowIfNull(distro);
        ReadOnlyMemory<byte>? keyData = null;
        if (distro.Iso.GpgKeyFile is { Length: > 0 } keyFile &&
            distro.SourceDirectory is { Length: > 0 } source)
        {
            var root = Path.GetFullPath(source) + Path.DirectorySeparatorChar;
            var keyPath = Path.GetFullPath(Path.Join(source, keyFile));
            if (!keyPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Signing key path escapes its provider directory.");
            if (File.Exists(keyPath)) keyData = File.ReadAllBytes(keyPath);
        }
        return new IsoSpecification(distro.Id, distro.Iso.DownloadUrl, distro.Iso.Sha256,
            distro.Iso.GpgSignatureUrl, distro.Iso.GpgKeyUrl, distro.Iso.GpgSignedDataUrl,
            keyData, distro.Iso.GpgKeyFingerprint, distro.Iso.IsoFilePattern);
    }
}
