using FluentAssertions;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public class DirectInstallViewModelTests
{
    private sealed class StubInstaller : IInstallationPreparation
    {
        public Task PrepareAsync(string distroId, int diskNumber, long linuxSizeBytes, string isoPath,
            string stagingDirectory, Uri? stage2Url,
            IProgress<DirectInstallProgress>? progress, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RegisterBootEntryAsync(
            IProgress<DirectInstallProgress>? progress, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubMaintenance : IWindowsMaintenance
    {
        public Task<int> StartBitLockerDecryptionAsync(string systemDrive,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
        public void RestartToFirmware() { }
        public void RestartToInstaller() { }
    }

    private static DirectInstallViewModel Create() => new(
        new StubInstaller(),
        new StubMaintenance(),
        NullLogger<DirectInstallViewModel>.Instance);

    [Fact]
    public void Logs_start_expanded_so_a_running_install_is_visible()
    {
        Create().LogsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Finishing_collapses_the_logs()
    {
        var vm = Create();

        vm.IsComplete = true;

        vm.LogsExpanded.Should().BeFalse();
    }

    // On the error panel the log is the answer, so it has to stay where it was.
    [Fact]
    public void Failing_leaves_the_logs_alone()
    {
        var vm = Create();

        vm.HasError = true;

        vm.LogsExpanded.Should().BeTrue();
    }

    [Fact]
    public void The_user_can_reopen_the_logs_after_it_finished()
    {
        var vm = Create();
        vm.IsComplete = true;

        vm.LogsExpanded = true;

        vm.LogsExpanded.Should().BeTrue();
    }
}
