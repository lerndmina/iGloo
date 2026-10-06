namespace Igloo.Command.Contracts;

/// <summary>The transport-independent invocation seam for V1 commands.</summary>
public interface ICommandDispatcher
{
    IReadOnlyList<CapabilityDescriptor> Capabilities { get; }
    Task<CommandResult> ExecuteAsync(CommandRequest request,
        IProgress<CommandProgress>? progress = null, CancellationToken cancellationToken = default);
}
