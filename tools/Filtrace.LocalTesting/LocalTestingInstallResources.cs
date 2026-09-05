// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Connects coordinator operations to the concrete baseline, CLI, MCP, and skill mutators.
/// </summary>
internal sealed class LocalTestingInstallResources : ILocalTestingInstallResources
{
    private readonly LocalTestingBaselineCapturer _baselineCapturer = new();
    private readonly LocalTestingCliInstaller _cliInstaller = new();
    private readonly LocalTestingMcpConfiguration _mcpConfiguration = new();
    private readonly LocalTestingSkillDirectory _skillDirectory = new();

    /// <inheritdoc />
    public LocalTestingBaseline CaptureBaseline(ResourcePlan plan)
    {
        return _baselineCapturer.Capture(plan);
    }

    /// <inheritdoc />
    public void ValidateBaseline(ResourcePlan plan, LocalTestingBaseline baseline)
    {
        ManagedPathGuard.EnsureNoLinks(plan.TargetRoot, plan.McpConfigurationPath);
        LocalTestingStateStore.ValidateMcpBaseline(baseline.Mcp);
        _ = LocalTestingSkillDirectory.ValidateBaseline(plan, baseline.Skill);
    }

    /// <inheritdoc />
    public CliInstallation InstallCli(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs,
        bool replaceExisting)
    {
        return replaceExisting
            ? _cliInstaller.InstallOrReplace(plan, inputs.CliPackagePath, inputs.DotnetPath)
            : _cliInstaller.InstallFresh(plan, inputs.CliPackagePath, inputs.DotnetPath);
    }

    /// <inheritdoc />
    public void PublishMcp(ResourcePlan plan, LocalTestingInstallInputs inputs)
    {
        _mcpConfiguration.Publish(plan, inputs.McpDllPath);
    }

    /// <inheritdoc />
    public void PublishSkill(ResourcePlan plan, LocalTestingInstallInputs inputs)
    {
        _skillDirectory.Publish(plan, inputs.SkillDirectory);
    }
}
