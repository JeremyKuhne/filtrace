// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

internal sealed class RecordingLocalTestingInstallResources : ILocalTestingInstallResources
{
    public List<string> Calls { get; } = [];

    public LocalTestingBaseline Baseline { get; set; } = TestState.Create(
        LocalTestingStatus.Installing).Baseline;

    public CliInstallation Cli { get; set; } = new()
    {
        PackageVersion = "2.0.0",
        PackageSha256 = TestState.Hash
    };

    public string? FailAt { get; set; }

    public Action<string, ResourcePlan>? BeforeCall { get; set; }

    public LocalTestingBaseline CaptureBaseline(ResourcePlan plan)
    {
        Record("capture", plan);
        return Baseline;
    }

    public void ValidateBaseline(ResourcePlan plan, LocalTestingBaseline baseline)
    {
        Record("validate", plan);
    }

    public CliInstallation InstallCli(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs,
        bool replaceExisting)
    {
        Record(replaceExisting ? "cli:replace" : "cli:fresh", plan);
        return Cli;
    }

    public void PublishMcp(ResourcePlan plan, LocalTestingInstallInputs inputs)
    {
        Record("mcp", plan);
    }

    public void PublishSkill(ResourcePlan plan, LocalTestingInstallInputs inputs)
    {
        Record("skill", plan);
    }

    private void Record(string operation, ResourcePlan plan)
    {
        Calls.Add(operation);
        BeforeCall?.Invoke(operation, plan);
        if (operation.Equals(FailAt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Injected {operation} failure.");
        }
    }
}
