// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Orders durable state transitions and resource publication for local-testing installation.
/// </summary>
internal sealed class LocalTestingCoordinator
{
    private readonly LocalTestingStateStore _stateStore;
    private readonly ILocalTestingInstallResources _resources;

    /// <summary>
    ///  Creates a coordinator backed by the concrete local-testing resource mutators.
    /// </summary>
    public LocalTestingCoordinator()
        : this(new(), new LocalTestingInstallResources())
    {
    }

    /// <summary>
    ///  Creates a coordinator with testable state and resource dependencies.
    /// </summary>
    /// <param name="stateStore">The durable state reader and writer.</param>
    /// <param name="resources">The resource-scoped installation operations.</param>
    internal LocalTestingCoordinator(
        LocalTestingStateStore stateStore,
        ILocalTestingInstallResources resources)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(resources);
        _stateStore = stateStore;
        _resources = resources;
    }

    /// <summary>
    ///  Runs Fresh Install, Resume Install, or Refresh according to the target's durable state.
    /// </summary>
    /// <param name="plan">The target's normalized local-testing resource paths.</param>
    /// <param name="inputs">Source-built inputs validated before target mutation.</param>
    /// <returns>The durable active state written after all resources are published.</returns>
    public LocalTestingState Install(ResourcePlan plan, LocalTestingInstallInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inputs);
        using LocalTestingTargetLock targetLock = LocalTestingTargetLock.Acquire(plan);
        ValidatePrivatePaths(plan);
        LocalTestingState? state = _stateStore.Read(plan.StatePath);
        LocalTestingOperation operation = LocalTestingOperationClassifier.Classify(
            LocalTestingAction.Install,
            state);

        return operation switch
        {
            LocalTestingOperation.FreshInstall => FreshInstall(plan, inputs),
            LocalTestingOperation.ResumeInstall => ResumeInstall(plan, inputs, state!),
            LocalTestingOperation.Refresh => Refresh(plan, inputs, state!),
            _ => throw new InvalidOperationException(
                $"Operation '{operation}' cannot install local-testing resources.")
        };
    }

    private LocalTestingState FreshInstall(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs)
    {
        PreparePrivateDirectories(plan);
        LocalTestingBaseline baseline = _resources.CaptureBaseline(plan);
        LocalTestingState installing = new()
        {
            SchemaVersion = LocalTestingState.CurrentSchemaVersion,
            Status = LocalTestingStatus.Installing,
            SourceCheckout = inputs.SourceCheckout,
            Baseline = baseline
        };

        _stateStore.Write(plan.StatePath, installing);
        return PublishResources(plan, inputs, installing, replaceExistingCli: false);
    }

    private LocalTestingState ResumeInstall(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs,
        LocalTestingState state)
    {
        ValidateContinuingInstall(plan, inputs, state);
        return PublishResources(plan, inputs, state, replaceExistingCli: true);
    }

    private LocalTestingState Refresh(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs,
        LocalTestingState state)
    {
        ValidateContinuingInstall(plan, inputs, state);
        _ = SkillOverlay.Read(plan.SkillDestination);
        LocalTestingState installing = state with
        {
            Status = LocalTestingStatus.Installing
        };

        _stateStore.Write(plan.StatePath, installing);
        return PublishResources(plan, inputs, installing, replaceExistingCli: true);
    }

    private LocalTestingState PublishResources(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs,
        LocalTestingState installing,
        bool replaceExistingCli)
    {
        CliInstallation cli = _resources.InstallCli(
            plan,
            inputs,
            replaceExistingCli);

        _resources.PublishMcp(plan, inputs);
        _resources.PublishSkill(plan, inputs);
        LocalTestingState active = installing with
        {
            Status = LocalTestingStatus.Active,
            Cli = cli
        };

        _stateStore.Write(plan.StatePath, active);
        return active;
    }

    private void ValidateContinuingInstall(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs,
        LocalTestingState state)
    {
        if (!PathsEqual(inputs.SourceCheckout, state.SourceCheckout))
        {
            throw new InvalidOperationException(
                $"Local testing for '{plan.TargetRoot}' is controlled by source checkout "
                    + $"'{state.SourceCheckout}', not '{inputs.SourceCheckout}'.");
        }

        _resources.ValidateBaseline(plan, state.Baseline);
    }

    private static void ValidatePrivatePaths(ResourcePlan plan)
    {
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.StateRoot);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.StatePath);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.ArtifactsDirectory);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.SkillBackupPath);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.CliDirectory);
        if (Directory.Exists(plan.StatePath))
        {
            throw new InvalidDataException(
                $"Local-testing state is a directory, not a file: '{plan.StatePath}'.");
        }

        _ = RegularFileGuard.Exists(plan.StatePath, "Local-testing state");
    }

    private static void PreparePrivateDirectories(ResourcePlan plan)
    {
        Directory.CreateDirectory(plan.ArtifactsDirectory);
        ValidatePrivatePaths(plan);
    }

    private static bool PathsEqual(string first, string second)
    {
        string normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        string normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return normalizedFirst.Equals(normalizedSecond, comparison);
    }
}
