using System.Text.Json;
using System.Xml.Linq;
using Igloo.Cli;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class CommandBoundaryTests
{
    private sealed class Checker : IPreflightChecker
    {
        public Task<PreflightReport> RunAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PreflightReport
            {
                IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
                BitLocker = BitLockerState.NotEncrypted, TotalRamBytes = 8_000_000_000,
                GpuVendor = "PRIVATE_GPU", Disks = [new("PRIVATE_DEVICE", "PRIVATE_DISK", 100, 50,
                    "GPT", [new(1, "NTFS", 100, "PRIVATE_LABEL", false, true)])],
                Findings = [new(FindingSeverity.Warning, "LOW_RAM", "PRIVATE_MESSAGE", "PRIVATE_PATH")],
            });
        }
    }

    private static CommandDispatcher Dispatcher() => new(new Checker(),
        new DistroLoader(NullLogger<DistroLoader>.Instance),
        new DistroRegistry(NullLogger<DistroRegistry>.Instance));

    private static CommandRequest Request(string capability, int protocol = 1, int version = 1) =>
        new(protocol, capability, version, Guid.NewGuid(), Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { }));

    [Fact]
    public async Task InspectionReturnsTypedAllowlistedFactsWithoutPrivateObservationText()
    {
        var result = await Dispatcher().ExecuteAsync(Request("system.inspect"));
        Assert.True(result.Succeeded);
        var data = result.Data!.Value.Deserialize<SystemInspection>(CommandProtocol.JsonOptions)!;
        Assert.True(data.IsUefi);
        Assert.Equal(1, data.DiskCount);
        Assert.Equal("LOW_RAM", Assert.Single(data.Findings).Code);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result, CommandProtocol.JsonOptions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogueAndVersionRejectUnknownOrMutatingCapabilities()
    {
        var dispatcher = Dispatcher();
        var catalogue = await dispatcher.ExecuteAsync(Request("system.capabilities"));
        Assert.True(catalogue.Succeeded);
        Assert.DoesNotContain(dispatcher.Capabilities, c => c.Kind == CommandKind.Mutation);
        Assert.Contains(dispatcher.Capabilities, c => c.Id == "migration.plan" && c.Kind == CommandKind.Plan);
        Assert.DoesNotContain(dispatcher.Capabilities, c => c.Id == "migration.execute");
        Assert.Equal(CommandErrorCode.UnsupportedCapability,
            (await dispatcher.ExecuteAsync(Request("migration.execute"))).Error!.Code);
        Assert.Equal(CommandErrorCode.UnsupportedVersion,
            (await dispatcher.ExecuteAsync(Request("system.inspect", protocol: 2))).Error!.Code);
        Assert.Equal(CommandErrorCode.UnsupportedVersion,
            (await dispatcher.ExecuteAsync(Request("system.inspect", version: 2))).Error!.Code);
        Assert.Equal(CommandErrorCode.InvalidRequest,
            (await dispatcher.ExecuteAsync(Request("system.inspect") with { Input = default })).Error!.Code);
    }

    [Fact]
    public async Task CancellationIsStructuredAndDoesNotCallChecker()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await Dispatcher().ExecuteAsync(Request("system.inspect"), cancellationToken: cts.Token);
        Assert.Equal(CommandErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task CliJsonIsOneParseableResultAndErrorsHaveStableExitCodes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["inspect", "--json"], Dispatcher(), new StringReader(""), output, error);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        using var inspected = JsonDocument.Parse(output.ToString());
        Assert.Equal("system.inspect", inspected.RootElement.GetProperty("capabilityId").GetString());
        output.GetStringBuilder().Clear();
        exit = await CliApplication.RunAsync(["distros", "list", "--json"], Dispatcher(),
            new StringReader(""), output, error);
        Assert.Equal(0, exit);
        Assert.Equal("distro.list", JsonDocument.Parse(output.ToString()).RootElement
            .GetProperty("capabilityId").GetString());
        output.GetStringBuilder().Clear();
        exit = await CliApplication.RunAsync(["capabilities", "list", "--json"], Dispatcher(),
            new StringReader(""), output, error);
        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("system.capabilities", document.RootElement.GetProperty("capabilityId").GetString());
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        output.GetStringBuilder().Clear();
        exit = await CliApplication.RunAsync(["unknown", "--json"], Dispatcher(), new StringReader(""), output, error);
        Assert.Equal(2, exit);
        Assert.Equal("InvalidRequest", JsonDocument.Parse(output.ToString()).RootElement
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task HumanCatalogueRendersCommandsWithoutJsonOrQueryProgress()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["capabilities", "list"], Dispatcher(),
            new StringReader(""), output, error);
        Assert.Equal(0, exit);
        Assert.Contains("system.inspect v1", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("{", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void ProjectReferencesKeepProtocolAndFleetServerIsolated()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln"))) root = root.Parent;
        Assert.NotNull(root);
        static string[] Refs(string path) => XDocument.Load(path).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value)).ToArray();
        string Project(string name) => Path.Join(root!.FullName, "src", name, name + ".csproj");
        Assert.Empty(Refs(Project("Igloo.Command.Contracts")));
        Assert.Equal(["Igloo.Command.Contracts", "Igloo.Core", "Igloo.Preflight"],
            Refs(Project("Igloo.Core.Runtime")).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Refs(Project("Igloo.Fleet.Server")),
            r => r is "Igloo.Core.Runtime" or "Igloo.Preflight" or "Igloo.Iso" or "Igloo.Migration");
        Assert.All(Directory.EnumerateFiles(Path.Join(root!.FullName, "distros"), "*.csproj", SearchOption.AllDirectories),
            path => Assert.All(Refs(path), r => Assert.Equal("Igloo.Core", r)));
        Assert.DoesNotContain("Igloo.Community.App", Refs(Project("Igloo.Core.Runtime.Execution")));
        Assert.DoesNotContain("Igloo.Fleet.Server", Refs(Project("Igloo.Core.Runtime.Execution")));
        Assert.Equal(["Igloo.Command.Contracts", "Igloo.Core.Host"],
            Refs(Path.Join(root.FullName, "src", "Igloo.Cli", "Igloo.Cli", "Igloo.Cli.csproj"))
                .Order(StringComparer.Ordinal));
        Assert.Equal(["Igloo.Command.Contracts", "Igloo.Core.Host"],
            Refs(Project("Igloo.Community.App")).Order(StringComparer.Ordinal));
        Assert.Equal(["Igloo.Command.Contracts", "Igloo.Core.Host", "Igloo.Fleet.Contracts", "Igloo.Fleet.Domain"],
            Refs(Project("Igloo.Fleet.Agent")).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Refs(Project("Igloo.Fleet.Server")), r => r is
            "Igloo.Core.Host" or "Igloo.Core.Runtime.Execution" or "Igloo.Core.Runtime" or
            "Igloo.Preflight" or "Igloo.Iso" or "Igloo.Migration" or "Igloo.UsbWriter");
        var host = XDocument.Load(Project("Igloo.Core.Host"));
        Assert.DoesNotContain(host.Descendants("UseWPF"), e => e.Value == "true");
        Assert.DoesNotContain(Refs(Project("Igloo.Core.Host")), r => r.StartsWith("Igloo.Fleet", StringComparison.Ordinal));
        foreach (var product in new[] { "Igloo.Community.App", "Igloo.Fleet.Agent" })
        {
            foreach (var source in Directory.EnumerateFiles(Path.Join(root.FullName, "src", product),
                         "*.cs", SearchOption.AllDirectories).Where(p =>
                         !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase)))
            {
                var text = File.ReadAllText(source);
                Assert.DoesNotContain("using Igloo.Preflight;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("using Igloo.Iso;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("using Igloo.Migration;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("using Igloo.UsbWriter;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("using Igloo.Core.Runtime.Execution;", text, StringComparison.Ordinal);
            }
        }
        foreach (var layer in new[] { "Igloo.Core", "Igloo.Core.Runtime", "Igloo.Core.Runtime.Execution", "Igloo.Core.Host" })
        {
            var project = XDocument.Load(Project(layer));
            Assert.DoesNotContain(project.Descendants("UseWPF"), e => e.Value == "true");
            Assert.DoesNotContain(Refs(Project(layer)), r => r is "Igloo.One.App" or
                "Igloo.Community.App" or "Igloo.Fleet.Server" || r.Contains("AI", StringComparison.OrdinalIgnoreCase));
        }
        var cliSource = File.ReadAllText(Path.Join(root.FullName, "src", "Igloo.Cli", "Igloo.Cli", "Program.cs"));
        Assert.DoesNotContain("IsoAcquisitionService", cliSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectInstallService", cliSource, StringComparison.Ordinal);
        var stagingView = File.ReadAllText(Path.Join(root.FullName, "src", "Igloo.Community.App",
            "ViewModels", "FileStagingViewModel.cs"));
        Assert.DoesNotContain("ManifestGeneratorService", stagingView, StringComparison.Ordinal);
        Assert.DoesNotContain("PluginArtifactWriter", stagingView, StringComparison.Ordinal);
        Assert.DoesNotContain("BrowserCredentialMigration", stagingView, StringComparison.Ordinal);
        var isoView = File.ReadAllText(Path.Join(root.FullName, "src", "Igloo.Community.App",
            "ViewModels", "IsoAcquisitionViewModel.cs"));
        Assert.DoesNotContain("IsoSpecification", isoView, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentHostConsumerDiscoversProvidersCompatibilityAndAdvisoryPlan()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalBackend(includeIsoAcquisition: false);
        services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(new Checker()));
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IBackendCatalog>().LoadAsync(Path.Join(RepositoryRoot(), "distros"));
        var commands = provider.GetRequiredService<ICommandDispatcher>();
        Assert.DoesNotContain(commands.Capabilities, c => c.Kind == CommandKind.Mutation);
        var list = await commands.ExecuteAsync(Request("distro.list"));
        Assert.Contains(list.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!.Distros,
            d => d.Id == "fedora-kde");
        var compatible = await commands.ExecuteAsync(Request("distro.compatibility") with
        { Input = JsonSerializer.SerializeToElement(new DistroSelection("fedora-kde"), CommandProtocol.JsonOptions) });
        Assert.True(compatible.Succeeded);
        var plan = await commands.ExecuteAsync(Request("migration.plan") with
        { Input = JsonSerializer.SerializeToElement(new MigrationPlanInput("fedora-kde", 0),
            CommandProtocol.JsonOptions) });
        Assert.True(plan.Succeeded);
        Assert.Equal(64, plan.ObservedStateFingerprint!.Length);
        Assert.False(plan.Data!.Value.Deserialize<MigrationPlanResult>(CommandProtocol.JsonOptions)!.Executable);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(plan, CommandProtocol.JsonOptions),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommunityIsoWorkflowAndCliCommandShareHostAcquisitionSemantics()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-host-iso-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var acquisition = new FakeIsoAcquisition();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalBackend();
            services.Replace(ServiceDescriptor.Singleton<IIsoAcquisitionService>(acquisition));
            services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(new Checker()));
            services.Replace(ServiceDescriptor.Singleton<IOperationJournal>(new FileOperationJournal(directory)));
            using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IBackendCatalog>().LoadAsync(Path.Join(RepositoryRoot(), "distros"));
            var distro = Assert.Single(provider.GetRequiredService<IProviderCatalog>().LoadedDistros,
                d => d.Id == "fedora-kde");
            var communityResult = await provider.GetRequiredService<IIsoWorkflow>().AcquireAsync(distro, null);
            Assert.True(communityResult.GpgVerified);
            var communitySpec = acquisition.LastSpec;
            var commands = provider.GetRequiredService<ICommandDispatcher>();
            var request = Request("iso.acquire") with
            { Input = JsonSerializer.SerializeToElement(new DistroSelection(distro.Id), CommandProtocol.JsonOptions) };
            var cliResult = await commands.ExecuteAsync(request);
            Assert.True(cliResult.Succeeded);
            Assert.Equal(communitySpec, acquisition.LastSpec);
            Assert.True(cliResult.Data!.Value.Deserialize<IsoAcquired>(CommandProtocol.JsonOptions)!.GpgVerified);
            Assert.DoesNotContain("PRIVATE_PATH", JsonSerializer.Serialize(cliResult, CommandProtocol.JsonOptions),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task JournalIsDurableRejectsReplayAndPublishesOnlySafeStatus()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-operation-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            var request = Request("iso.acquire") with { OperationId = Guid.NewGuid() };
            await journal.CreateAsync(request, "ABCD");
            await journal.TransitionAsync(request.OperationId!.Value, LocalOperationState.Running,
                "iso.acquiring");
            Assert.Throws<ResourceBusyException>(() => journal.CreateAsync(request, "ABCD")
                .GetAwaiter().GetResult());
            var reopened = new FileOperationJournal(directory);
            var status = await new OperationStatusHandler(reopened, false).ExecuteAsync(
                Request("operation.status") with
                { Input = JsonSerializer.SerializeToElement(new OperationSelection(request.OperationId.Value),
                    CommandProtocol.JsonOptions) }, null, CancellationToken.None);
            Assert.Equal("Running", Assert.IsType<OperationStatus>(status).State);
            await journal.TransitionAsync(request.OperationId.Value, LocalOperationState.Completed,
                "iso.verified", ["Sha256Verified"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TransitionAsync(
                request.OperationId.Value, LocalOperationState.Running, "iso.acquiring"));
            var bytes = await File.ReadAllTextAsync(Path.Join(directory,
                request.OperationId.Value.ToString("N") + ".json"));
            Assert.DoesNotContain("PRIVATE", bytes, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task StateRevisionChangesWithRelevantDiskObservationAndLocksAreCrossCall()
    {
        var first = await new Checker().RunAsync();
        var fingerprint = ObservedStateRevision.Fingerprint(first);
        Assert.True(ObservedStateRevision.Matches(first, fingerprint));
        Assert.False(ObservedStateRevision.Matches(first with { SecureBootEnabled = true }, fingerprint));
        using var lease = await ResourceLock.AcquireManyAsync(["disk:fixture", "partition-table:fixture"]);
        await Assert.ThrowsAsync<ResourceBusyException>(async () =>
        {
            using var denied = await ResourceLock.AcquireAsync("disk:fixture");
        });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ResourceLock.AcquireAsync("unrelated", cancelled.Token));
    }

    [Fact]
    public async Task CommunityCoordinatorAndCommandUseSameVerifiedIsoPath()
    {
        var root = RepositoryRoot();
        var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
        loader.Load(Path.Join(root, "distros"));
        var distro = Assert.Single(loader.LoadedDistros, d => d.Id == "fedora-kde");
        var fake = new FakeIsoAcquisition();
        var coordinator = new IsoAcquisitionCoordinator(fake);
        var direct = await coordinator.AcquireAsync(distro, null);
        Assert.True(direct.Sha256Verified);
        var first = fake.LastSpec;
        var dispatcher = new CommandDispatcher(new Checker(), loader,
            new DistroRegistry(NullLogger<DistroRegistry>.Instance),
            [new IsoAcquireHandler(loader, coordinator)]);
        var request = new CommandRequest(1, "iso.acquire", 1, Guid.NewGuid(), Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new DistroSelection(distro.Id), CommandProtocol.JsonOptions));
        var result = await dispatcher.ExecuteAsync(request);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.OperationId);
        Assert.Equal(first, fake.LastSpec);
        Assert.DoesNotContain("PRIVATE_PATH", JsonSerializer.Serialize(result, CommandProtocol.JsonOptions), StringComparison.Ordinal);
        var data = result.Data!.Value.Deserialize<IsoAcquired>(CommandProtocol.JsonOptions)!;
        Assert.True(data.GpgVerified);
        using var lease = await ResourceLock.AcquireAsync("iso:" + distro.Id);
        var busy = await dispatcher.ExecuteAsync(request with { RequestId = Guid.NewGuid() });
        Assert.Equal(CommandErrorCode.ResourceBusy, busy.Error!.Code);
    }

    [Fact]
    public async Task FixedInvokeRequestHasStableJsonEnvelope()
    {
        var request = new CommandRequest(1, "distro.list", 1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            JsonSerializer.SerializeToElement(new { }));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["invoke"], Dispatcher(),
            new StringReader(JsonSerializer.Serialize(request, CommandProtocol.JsonOptions)), output, error);
        Assert.Equal(0, exit);
        Assert.Equal("{\"protocolVersion\":1,\"capabilityId\":\"distro.list\",\"capabilityVersion\":1," +
            "\"requestId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"correlationId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"succeeded\":true,\"data\":{\"distros\":[]},\"warnings\":[],\"evidence\":[]}" + Environment.NewLine,
            output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void PublishedV1SchemasAreValidJsonAndCoverTheImplementedPayloads()
    {
        var directory = Path.Join(RepositoryRoot(), "docs", "spec", "command-contract");
        foreach (var name in new[] { "request", "result", "capability", "capabilities" })
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, name + ".schema.json")));
            Assert.Equal("https://json-schema.org/draft/2020-12/schema",
                schema.RootElement.GetProperty("$schema").GetString());
        }
        using var payloads = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, "capabilities.schema.json")));
        foreach (var name in new[] { "distroSelectionInput", "distroListResult",
            "systemInspectionResult", "isoAcquiredResult", "migrationInspectionResult",
            "migrationPlanInput", "migrationPlanResult", "recoveryInspectionResult",
            "operationSelectionInput", "operationStatusResult" })
            Assert.True(payloads.RootElement.GetProperty("$defs").TryGetProperty(name, out _));
    }

    [Fact]
    public async Task StagingCoordinatorWritesPrivateManifestAfterCopyWithoutUiExecution()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-command-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new FakeStaging(directory);
            var hooks = new MigrationStagingHooks(
                _ =>
                {
                    Assert.True(service.Called);
                    return Task.FromResult<IReadOnlyList<WifiNetwork>>([]);
                },
                (browsers, _, _) => Task.FromResult(browsers),
                (manifest, _) => manifest,
                (manifest, _) => manifest);
            var coordinator = new MigrationStagingCoordinator(service,
                new DistroRegistry(NullLogger<DistroRegistry>.Instance),
                NullLogger<MigrationStagingCoordinator>.Instance, hooks);
            var user = new UserSetup { WindowsUsername = "fixture", LinuxUsername = "fixture",
                LinuxPassword = "fixture-secret" };
            var result = await coordinator.StageAsync(new(new FileStagingRequest("fixture-distro", []),
                user, (await new Checker().RunAsync()), null, DiskInstallMode.ReplaceDisk, 0), null);
            Assert.Equal(directory, result.StagingDirectory);
            var manifest = await File.ReadAllTextAsync(Path.Join(directory, "migration-manifest.json"));
            Assert.Contains("fixture-distro", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-secret", manifest, StringComparison.Ordinal);
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), full, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(full, recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln"))) root = root.Parent;
        return root?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static void DeleteFixtureDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var temp = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temp, full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("igloo-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private sealed class FakeIsoAcquisition : IIsoAcquisitionService
    {
        public IsoSpecification? LastSpec { get; private set; }
        public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default)
        {
            LastSpec = spec;
            return Task.FromResult(new IsoAcquisitionResult("PRIVATE_PATH", true, true, 123));
        }
    }

    private sealed class FakeStaging(string directory) : IFileStagingService
    {
        public bool Called { get; private set; }
        public Task<FileStagingResult> StageAsync(FileStagingRequest request,
            IProgress<FileStagingProgress>? progress, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(new FileStagingResult(directory, 0, 0));
        }
    }
}
