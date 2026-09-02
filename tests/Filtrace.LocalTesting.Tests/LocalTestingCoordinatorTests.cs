// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingCoordinatorTests
{
    [TestMethod]
    public void Install_FreshInstall_CapturesBaselineBeforePublishingInOrder()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new();
        resources.BeforeCall = (operation, currentPlan) =>
        {
            LocalTestingState? durable = store.Read(currentPlan.StatePath);
            if (operation is "capture")
            {
                durable.Should().BeNull();
                Directory.Exists(currentPlan.ArtifactsDirectory).Should().BeTrue();
            }
            else
            {
                durable!.Status.Should().Be(LocalTestingStatus.Installing);
            }
        };

        LocalTestingState result = new LocalTestingCoordinator(store, resources).Install(
            plan,
            inputs);

        resources.Calls.Should().Equal("capture", "cli:fresh", "mcp", "skill");
        result.Status.Should().Be(LocalTestingStatus.Active);
        result.SourceCheckout.Should().Be(inputs.SourceCheckout);
        result.Baseline.Should().BeEquivalentTo(resources.Baseline);
        result.Cli.Should().BeEquivalentTo(resources.Cli);
        store.Read(plan.StatePath).Should().BeEquivalentTo(result);
    }

    [TestMethod]
    [DataRow("cli:fresh")]
    [DataRow("mcp")]
    [DataRow("skill")]
    public void Install_FreshResourceFailure_LeavesInstallingBaseline(string failure)
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new()
        {
            FailAt = failure
        };

        Action install = () => new LocalTestingCoordinator(store, resources).Install(plan, inputs);

        install.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Injected {failure} failure*");

        LocalTestingState durable = store.Read(plan.StatePath)!;
        durable.Status.Should().Be(LocalTestingStatus.Installing);
        durable.SourceCheckout.Should().Be(inputs.SourceCheckout);
        durable.Baseline.Should().BeEquivalentTo(resources.Baseline);
        durable.Cli.Should().BeNull();
    }

    [TestMethod]
    public void Install_ResumeInstall_ReusesBaselineAndConvergesAfterPartialFailure()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new()
        {
            FailAt = "mcp"
        };

        Action firstInstall = () => new LocalTestingCoordinator(store, resources).Install(
            plan,
            inputs);

        firstInstall.Should().Throw<InvalidOperationException>();
        LocalTestingBaseline baseline = store.Read(plan.StatePath)!.Baseline;
        resources.Calls.Clear();
        resources.FailAt = null;

        LocalTestingState result = new LocalTestingCoordinator(store, resources).Install(
            plan,
            inputs);

        resources.Calls.Should().Equal("validate", "cli:replace", "mcp", "skill");
        result.Status.Should().Be(LocalTestingStatus.Active);
        result.Baseline.Should().BeEquivalentTo(baseline);
        result.Cli.Should().BeEquivalentTo(resources.Cli);
    }

    [TestMethod]
    public void Install_Refresh_PreservesExactBaselineAndTransitionsBeforePublishing()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new();
        WriteState(plan, store, inputs, LocalTestingStatus.Active, resources.Baseline);
        string baselineJson = ReadBaselineJson(plan.StatePath);
        resources.BeforeCall = (operation, currentPlan) =>
        {
            LocalTestingStatus expected = operation is "validate"
                ? LocalTestingStatus.Active
                : LocalTestingStatus.Installing;

            store.Read(currentPlan.StatePath)!.Status.Should().Be(expected);
        };

        LocalTestingState result = new LocalTestingCoordinator(store, resources).Install(
            plan,
            inputs);

        resources.Calls.Should().Equal("validate", "cli:replace", "mcp", "skill");
        result.Status.Should().Be(LocalTestingStatus.Active);
        result.Baseline.Should().BeEquivalentTo(resources.Baseline);
        result.Cli.Should().BeEquivalentTo(resources.Cli);
        ReadBaselineJson(plan.StatePath).Should().Be(baselineJson);
    }

    [TestMethod]
    public void Install_FailedRefresh_ResumesWithoutRecapturingBaseline()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new()
        {
            FailAt = "skill"
        };

        WriteState(plan, store, inputs, LocalTestingStatus.Active, resources.Baseline);
        string baselineJson = ReadBaselineJson(plan.StatePath);
        Action refresh = () => new LocalTestingCoordinator(store, resources).Install(plan, inputs);

        refresh.Should().Throw<InvalidOperationException>();
        store.Read(plan.StatePath)!.Status.Should().Be(LocalTestingStatus.Installing);
        ReadBaselineJson(plan.StatePath).Should().Be(baselineJson);
        resources.Calls.Clear();
        resources.FailAt = null;

        LocalTestingState result = new LocalTestingCoordinator(store, resources).Install(
            plan,
            inputs);

        resources.Calls.Should().Equal("validate", "cli:replace", "mcp", "skill");
        result.Status.Should().Be(LocalTestingStatus.Active);
        ReadBaselineJson(plan.StatePath).Should().Be(baselineJson);
    }

    [TestMethod]
    [DataRow((int)LocalTestingStatus.Installing)]
    [DataRow((int)LocalTestingStatus.Active)]
    public void Install_DifferentSourceCheckout_RejectsWithoutTransition(int statusValue)
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs original = LocalTestingInstallTestData.CreateInputs(
            directory.Path,
            "source-a");

        LocalTestingInstallInputs different = LocalTestingInstallTestData.CreateInputs(
            directory.Path,
            "source-b");

        LocalTestingStatus status = (LocalTestingStatus)statusValue;
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new();
        WriteState(plan, store, original, status, resources.Baseline);

        Action install = () => new LocalTestingCoordinator(store, resources).Install(
            plan,
            different);

        install.Should().Throw<InvalidOperationException>()
            .WithMessage("*controlled by source checkout*");

        store.Read(plan.StatePath)!.Status.Should().Be(status);
        resources.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public void Install_InvalidRefreshOverlay_LeavesActiveStateUntouched()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new();
        WriteState(plan, store, inputs, LocalTestingStatus.Active, resources.Baseline);
        Directory.CreateDirectory(plan.SkillDestination);
        using (FileStream stream = File.Create(Path.Join(plan.SkillDestination, "overlay.md")))
        {
            stream.SetLength(SkillOverlay.MaxBytes + 1);
        }

        Action install = () => new LocalTestingCoordinator(store, resources).Install(plan, inputs);

        install.Should().Throw<InvalidDataException>()
            .WithMessage("*overlay exceeds*safety limit*");

        store.Read(plan.StatePath)!.Status.Should().Be(LocalTestingStatus.Active);
        resources.Calls.Should().Equal("validate");
    }

    [TestMethod]
    public void Install_InvalidBaseline_LeavesActiveStateUntouched()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new()
        {
            FailAt = "validate"
        };

        WriteState(plan, store, inputs, LocalTestingStatus.Active, resources.Baseline);

        Action install = () => new LocalTestingCoordinator(store, resources).Install(plan, inputs);

        install.Should().Throw<InvalidOperationException>()
            .WithMessage("*Injected validate failure*");

        store.Read(plan.StatePath)!.Status.Should().Be(LocalTestingStatus.Active);
        resources.Calls.Should().Equal("validate");
    }

    [TestMethod]
    public void Install_InvalidManagedAncestor_LeavesActiveResourcesUntouched()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        LocalTestingBaseline baseline = TestState.Create(LocalTestingStatus.Active).Baseline;
        WriteState(plan, store, inputs, LocalTestingStatus.Active, baseline);
        File.WriteAllText(Path.Join(plan.TargetRoot, ".vscode"), "not a directory");

        Action install = () => new LocalTestingCoordinator(
            store,
            new LocalTestingInstallResources()).Install(plan, inputs);

        install.Should().Throw<InvalidDataException>()
            .WithMessage("*ancestor is not a directory*");

        store.Read(plan.StatePath)!.Status.Should().Be(LocalTestingStatus.Active);
        Directory.Exists(plan.CliDirectory).Should().BeFalse();
    }

    [TestMethod]
    public void Install_CaptureFailure_DoesNotWriteInstallingState()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        LocalTestingStateStore store = new();
        RecordingLocalTestingInstallResources resources = new()
        {
            FailAt = "capture"
        };

        Action install = () => new LocalTestingCoordinator(store, resources).Install(plan, inputs);

        install.Should().Throw<InvalidOperationException>();
        store.Read(plan.StatePath).Should().BeNull();
        resources.Calls.Should().Equal("capture");
    }

    [TestMethod]
    public void Install_StatePathDirectory_RejectsBeforeCapture()
    {
        using TemporaryDirectory directory = new();
        ResourcePlan plan = CreatePlan(directory);
        LocalTestingInstallInputs inputs = LocalTestingInstallTestData.CreateInputs(directory.Path);
        Directory.CreateDirectory(plan.StatePath);
        RecordingLocalTestingInstallResources resources = new();

        Action install = () => new LocalTestingCoordinator(
            new LocalTestingStateStore(),
            resources).Install(plan, inputs);

        install.Should().Throw<InvalidDataException>()
            .WithMessage("*state is a directory*");

        resources.Calls.Should().BeEmpty();
    }

    private static ResourcePlan CreatePlan(TemporaryDirectory directory)
    {
        string targetRoot = Path.Join(directory.Path, "target");
        string gitDirectory = Path.Join(targetRoot, ".git");
        Directory.CreateDirectory(gitDirectory);
        return ResourcePlan.Create(targetRoot, gitDirectory);
    }

    private static void WriteState(
        ResourcePlan plan,
        LocalTestingStateStore store,
        LocalTestingInstallInputs inputs,
        LocalTestingStatus status,
        LocalTestingBaseline baseline)
    {
        Directory.CreateDirectory(plan.StateRoot);
        LocalTestingState state = TestState.Create(status) with
        {
            SourceCheckout = inputs.SourceCheckout,
            Baseline = baseline
        };

        store.Write(plan.StatePath, state);
    }

    private static string ReadBaselineJson(string statePath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statePath));
        return document.RootElement.GetProperty("baseline").GetRawText();
    }
}
