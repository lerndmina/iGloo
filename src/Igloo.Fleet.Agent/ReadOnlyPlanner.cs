using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Fleet.Contracts;

namespace Igloo.Fleet.Agent;

/// <summary>Only preflight and plugin compatibility are reachable. No installer/render/write interface is invoked.</summary>
public sealed class ReadOnlyPlanner(IEndpointInspection inspection, IProviderCompatibility providers)
{
    public async Task<ReadOnlyWorkResult> RunAsync(ReadOnlyWorkItem work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.PayloadVersion != 1 || !Enum.IsDefined(work.Type) || work.LeaseId is null ||
            (work.Type == ReadOnlyWorkType.RunMigrationDryRun && work.Profile?.SchemaVersion != 1))
            throw new InvalidDataException("Unsupported read-only work.");
        var started = DateTimeOffset.UtcNow;
        try { return await ObserveAsync(work, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            var failed = new AssessmentResult(FleetProtocolVersion.Current, work.WorkItemId, work.Identity,
                started, DateTimeOffset.UtcNow, ReadOnlyAssessment.AgentVersion, ReadOnlyAssessment.IglooVersion,
                1, new(0, 0), [], EligibilityStatus.NeedsReview, AssessmentOutcome.LocalExecutionFailed);
            return new(PlanningProtocol.Version, work.WorkItemId, work.LeaseId.Value, work.CorrelationId,
                work.Profile?.RevisionId, failed, null);
        }
    }

    private async Task<ReadOnlyWorkResult> ObserveAsync(ReadOnlyWorkItem work, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var report = await inspection.InspectAsync(ct).ConfigureAwait(false);
        var assessment = ReadOnlyAssessment.Map(report, work.Identity, started) with { AssessmentId = work.WorkItemId };
        DryRunFacts? facts = null;
        if (work.Type == ReadOnlyWorkType.RunMigrationDryRun)
        {
            var spec = work.Profile!.Spec;
            var reasons = new HashSet<PlanningReason>();
            var supported = providers.IsLoaded(spec.DistroId) && Igloo.Fleet.Domain.PlanningService.SupportedDistro(spec.DistroId);
            var required = spec.MinimumAvailableBytes;
            string? pluginHash = null;
            if (supported)
            {
                var provider = await providers.InspectAsync(spec.DistroId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Distro provider became unavailable.");
                required = Math.Max(required, provider.MinimumDiskBytes);
                // Only this read-only plugin method is called. No rendering or payload staging.
                var findings = providers.Evaluate(spec.DistroId, report);
                if (findings.Any(f => f.Severity == FindingSeverity.Blocker)) reasons.Add(PlanningReason.HardwareUnsupported);
                if (findings.Any(f => f.Severity == FindingSeverity.Warning)) reasons.Add(PlanningReason.CompatibilityReview);
                pluginHash = provider.ImplementationHash;
            }
            else reasons.Add(PlanningReason.UnsupportedDistro);
            // A deterministic candidate: the single disk with Windows' boot partition.
            // GetSupportedSize values are observations, not permission to shrink.
            var available = AvailableCapacity(report.Disks, required);
            facts = new(supported, report.SecureBootEnabled, available, required, pluginHash, [.. reasons.Order()]);
        }
        return new(PlanningProtocol.Version, work.WorkItemId, work.LeaseId!.Value, work.CorrelationId,
            work.Profile?.RevisionId, assessment, facts);
    }

    public static long? AvailableCapacity(IReadOnlyList<DiskInfo> disks, long required)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var candidates = disks.Where(d => d.Partitions.Any(p => p.IsBoot)).ToArray();
        if (candidates.Length != 1) return null;
        var disk = candidates[0];
        if (disk.TotalBytes <= 0) return null;
        if (disk.TotalBytes < required) return disk.TotalBytes; // Definitive physical upper bound.
        var shrinkable = disk.Partitions.Where(p => p.IsBoot).Select(p => p.ShrinkableBytes).DefaultIfEmpty(0).Max();
        // Zero means either no shrink capacity OR a failed GetSupportedSize query in shared Preflight.
        // A small unallocated region does not prove the Windows partition cannot release more space.
        if (disk.FreeBytes < required && shrinkable <= 0) return null;
        return Math.Max(disk.FreeBytes, shrinkable);
    }
}
