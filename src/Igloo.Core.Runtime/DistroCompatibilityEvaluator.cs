using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;

namespace Igloo.Core.Runtime;

/// <summary>One provider compatibility evaluation for CLI, Community and Fleet local callers.</summary>
public sealed class DistroCompatibilityEvaluator(DistroRegistry registry)
{
    public IReadOnlyList<PreflightFinding> Evaluate(string distroId, PreflightReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroId);
        ArgumentNullException.ThrowIfNull(report);
        if (!registry.TryGet(distroId, out var plugin)) throw new KeyNotFoundException();
        return plugin.CheckCompatibility(report);
    }
}
