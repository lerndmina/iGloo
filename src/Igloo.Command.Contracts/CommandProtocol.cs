using System.Text.Json;
using System.Text.Json.Serialization;

namespace Igloo.Command.Contracts;

public static class CommandProtocol
{
    public const int Version = 1;
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public enum CommandKind { Query, Plan, Mutation }
public enum RiskClass { None, Low, Elevated, Destructive }
public enum AuthorityRequirement { User, Administrator, ExplicitApproval }
public enum CommandErrorCode
{
    InvalidRequest, UnsupportedCapability, UnsupportedVersion, PreconditionFailed,
    StateChanged, ApprovalRequired, PolicyDenied, ResourceBusy, NotSupported,
    VerificationFailed, RecoveryRequired, Cancelled, InternalFailure,
}

public sealed record CapabilityDescriptor(
    string Id, int Version, int InputSchemaVersion, int ResultSchemaVersion,
    CommandKind Kind, RiskClass Risk, bool SupportsDryRun,
    AuthorityRequirement Authority, IReadOnlyList<string> SupportedProviders,
    bool RequiresObservedState = false)
{
    public string InputSchemaId => $"igloo.command.{Id}.input.v{InputSchemaVersion}";
    public string ResultSchemaId => $"igloo.command.{Id}.result.v{ResultSchemaVersion}";
}

public sealed record CommandRequest(
    int ProtocolVersion, string CapabilityId, int CapabilityVersion,
    Guid RequestId, Guid CorrelationId, JsonElement Input,
    bool DryRun = false, string? IdempotencyKey = null,
    string? ObservedStateFingerprint = null, Guid? OperationId = null);

public sealed record CommandError(CommandErrorCode Code, string Message, string? DetailCode = null);
public sealed record CommandWarning(string Code, string Message);
public sealed record ExecutionEvidence(string Code, string Value);

public sealed record CommandResult(
    int ProtocolVersion, string CapabilityId, int CapabilityVersion,
    Guid RequestId, Guid CorrelationId, Guid? OperationId, bool Succeeded,
    JsonElement? Data, CommandError? Error,
    IReadOnlyList<CommandWarning> Warnings,
    IReadOnlyList<ExecutionEvidence> Evidence,
    string? ObservedStateFingerprint = null);

public sealed record CommandProgress(Guid RequestId, Guid? OperationId,
    string Phase, long? Completed = null, long? Total = null);

public sealed record SystemInspection(bool IsUefi, bool SecureBootEnabled,
    bool TpmPresent, string BitLockerState, long TotalRamBytes,
    int DiskCount, IReadOnlyList<InspectionFinding> Findings);
public sealed record InspectionFinding(string Code, string Severity);
public sealed record DistroSummary(string Id, string DisplayName, string? Status);
public sealed record DistroList(IReadOnlyList<DistroSummary> Distros);
public sealed record DistroSelection(string DistroId);
public sealed record CapabilitySelection(string CapabilityId);
public sealed record DistroCompatibility(string DistroId,
    IReadOnlyList<InspectionFinding> Findings, bool HasBlockers);
public sealed record IsoAcquired(string DistroId, bool Sha256Verified,
    bool GpgVerified, long SizeBytes);

public sealed record MigrationInspection(int WindowsBootDiskCount, long? ObservedAvailableBytes,
    IReadOnlyList<string> BlockerCodes, bool CanRequestAdvisoryPlan);
public sealed record MigrationPlanInput(string DistroId, long RequestedLinuxBytes);
public sealed record MigrationPlanResult(string DistroId, long RequiredBytes,
    long? ObservedAvailableBytes, IReadOnlyList<string> BlockerCodes,
    bool Executable, string Disposition);
public sealed record RecoveryInspection(string Support, string ReasonCode,
    bool HasExactSnapshot);
public sealed record OperationSelection(Guid OperationId);
public sealed record OperationStatus(Guid OperationId, string CapabilityId, string State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string CurrentStage,
    IReadOnlyList<string> EvidenceCodes, string? FailureCode, string? ContinuationCode);
