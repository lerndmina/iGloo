using System.Text.Json;
using Igloo.Command.Contracts;
using Igloo.Core.Host;

namespace Igloo.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        var backend = await IglooHost.CreateAsync(FindDistrosDirectory(),
            cancellation.Token).ConfigureAwait(false);
        await using var backendDisposal = backend.ConfigureAwait(false);
        return await CliApplication.RunAsync(args, backend.Commands, Console.In, Console.Out,
            Console.Error, cancellation.Token).ConfigureAwait(false);
    }

    private static string FindDistrosDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Join(current.FullName, "distros");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return Path.Join(AppContext.BaseDirectory, "distros");
    }
}

internal static class CliApplication
{
    private static readonly JsonSerializerOptions HumanJson = new(CommandProtocol.JsonOptions) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, ICommandDispatcher dispatcher,
        TextReader input, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var json = args.Contains("--json", StringComparer.Ordinal);
        var positional = args.Where(a => a != "--json").ToArray();
        CommandRequest request;
        try
        {
            if (positional is ["invoke"])
            {
                json = true;
                request = JsonSerializer.Deserialize<CommandRequest>(
                    await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false),
                    CommandProtocol.JsonOptions) ?? throw new JsonException();
            }
            else
            {
                var capability = ResolveCapability(positional);
                object value = positional is ["distros", "inspect" or "compatibility", _]
                    or ["iso", "acquire", _]
                    ? new DistroSelection(positional[2])
                    : positional is ["migration", "plan", _, ..]
                        ? new MigrationPlanInput(positional[2], positional.Length == 4
                            ? long.Parse(positional[3], System.Globalization.CultureInfo.InvariantCulture) : 0)
                    : positional is ["operations", "status", _] or ["migration", "status", _]
                        ? new OperationSelection(Guid.Parse(positional[2]))
                    : positional is ["capabilities", "describe", _]
                        ? new CapabilitySelection(positional[2]) : new { };
                request = new(CommandProtocol.Version, capability, 1, Guid.NewGuid(), Guid.NewGuid(),
                    JsonSerializer.SerializeToElement(value, CommandProtocol.JsonOptions));
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or OverflowException)
        {
            var bad = new CommandResult(CommandProtocol.Version, "", 0, Guid.NewGuid(), Guid.NewGuid(),
                null, false, null, new(CommandErrorCode.InvalidRequest, "Invalid command or JSON request."), [], []);
            await RenderAsync(bad, json, output, error).ConfigureAwait(false);
            return 2;
        }
        IProgress<CommandProgress>? progress = dispatcher.Capabilities.Any(c =>
            c.Id == request.CapabilityId && c.Kind == CommandKind.Mutation)
            ? new Progress<CommandProgress>(p =>
            {
                if (!json) error.WriteLine(p.Phase);
            })
            : null;
        var result = await dispatcher.ExecuteAsync(request, progress, cancellationToken).ConfigureAwait(false);
        await RenderAsync(result, json, output, error).ConfigureAwait(false);
        return result.Error?.Code switch
        {
            null => 0,
            CommandErrorCode.InvalidRequest or CommandErrorCode.UnsupportedCapability
                or CommandErrorCode.UnsupportedVersion => 2,
            CommandErrorCode.Cancelled => 130,
            CommandErrorCode.InternalFailure => 4,
            _ => 3,
        };
    }

    private static string ResolveCapability(string[] args)
    {
        return args switch
        {
            ["version"] => "system.version",
            ["inspect"] => "system.inspect",
            ["capabilities", "list"] => "system.capabilities",
            ["capabilities", "describe", _] => "system.capability.describe",
            ["distros", "list"] => "distro.list",
            ["distros", "inspect", _] => "distro.inspect",
            ["distros", "compatibility", _] => "distro.compatibility",
            ["iso", "acquire", _] => "iso.acquire",
            ["migration", "inspect"] => "migration.inspect",
            ["migration", "plan", _] or ["migration", "plan", _, _] => "migration.plan",
            ["migration", "status", _] => "migration.status",
            ["operations", "status", _] => "operation.status",
            ["recovery", "inspect"] => "recovery.inspect",
            _ => throw new ArgumentException("Unknown command."),
        };
    }

    private static async Task RenderAsync(CommandResult result, bool json,
        TextWriter output, TextWriter error)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(result, CommandProtocol.JsonOptions))
                .ConfigureAwait(false);
            return;
        }
        if (result.Error is not null)
        {
            await error.WriteLineAsync($"{result.Error.Code}: {result.Error.Message}").ConfigureAwait(false);
            return;
        }
        switch (result.CapabilityId)
        {
            case "system.inspect":
                var machine = result.Data!.Value.Deserialize<SystemInspection>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"UEFI: {machine.IsUefi}; Secure Boot: {machine.SecureBootEnabled}; TPM: {machine.TpmPresent}")
                    .ConfigureAwait(false);
                await output.WriteLineAsync($"BitLocker: {machine.BitLockerState}; RAM: {machine.TotalRamBytes} bytes; disks: {machine.DiskCount}")
                    .ConfigureAwait(false);
                foreach (var finding in machine.Findings)
                    await output.WriteLineAsync($"{finding.Severity}: {finding.Code}").ConfigureAwait(false);
                break;
            case "distro.list":
                var distros = result.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!;
                foreach (var distro in distros.Distros)
                    await output.WriteLineAsync($"{distro.Id} - {distro.DisplayName} ({distro.Status ?? "available"})")
                        .ConfigureAwait(false);
                break;
            case "distro.inspect":
                var inspected = result.Data!.Value.Deserialize<DistroSummary>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{inspected.DisplayName} [{inspected.Id}] ({inspected.Status ?? "available"})")
                    .ConfigureAwait(false);
                break;
            case "distro.compatibility":
                var compatibility = result.Data!.Value.Deserialize<DistroCompatibility>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{compatibility.DistroId}: {(compatibility.HasBlockers ? "blocked" : "no blocker found")}")
                    .ConfigureAwait(false);
                foreach (var finding in compatibility.Findings)
                    await output.WriteLineAsync($"{finding.Severity}: {finding.Code}").ConfigureAwait(false);
                break;
            case "iso.acquire":
                var iso = result.Data!.Value.Deserialize<IsoAcquired>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{iso.DistroId}: acquired {iso.SizeBytes} bytes; SHA-256 verified: {iso.Sha256Verified}; GPG verified: {iso.GpgVerified}")
                    .ConfigureAwait(false);
                break;
            case "migration.inspect":
                var migration = result.Data!.Value.Deserialize<MigrationInspection>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"Windows boot disks: {migration.WindowsBootDiskCount}; observed capacity: {migration.ObservedAvailableBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} bytes")
                    .ConfigureAwait(false);
                foreach (var blocker in migration.BlockerCodes)
                    await output.WriteLineAsync($"Review: {blocker}").ConfigureAwait(false);
                break;
            case "migration.plan":
                var plan = result.Data!.Value.Deserialize<MigrationPlanResult>(CommandProtocol.JsonOptions)!;
                await output.WriteLineAsync($"{plan.DistroId}: {plan.Disposition}; required {plan.RequiredBytes} bytes; observed {plan.ObservedAvailableBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} bytes")
                    .ConfigureAwait(false);
                foreach (var blocker in plan.BlockerCodes)
                    await output.WriteLineAsync($"Blocker: {blocker}").ConfigureAwait(false);
                break;
            case "system.capabilities":
                var catalogue = result.Data!.Value.Deserialize<CapabilityDescriptor[]>(CommandProtocol.JsonOptions)!;
                foreach (var capability in catalogue)
                    await output.WriteLineAsync($"{capability.Id} v{capability.Version} ({capability.Kind}, {capability.Risk})")
                        .ConfigureAwait(false);
                break;
            default:
                await output.WriteLineAsync(JsonSerializer.Serialize(result.Data, HumanJson)).ConfigureAwait(false);
                break;
        }
    }
}
