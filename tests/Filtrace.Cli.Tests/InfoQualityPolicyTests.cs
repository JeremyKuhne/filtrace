// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli;

[TestClass]
public sealed class InfoQualityPolicyTests
{
    [TestMethod]
    public void Evaluate_EnabledZero_EnabledPassesEventsFails()
    {
        TraceInfo info = CreateInfo(
            resolutionRate: 1.0,
            sampleCount: 10,
            "cpu",
            new AnalysisAvailability(FormatSupported: true, CaptureStatus.Enabled, 0));

        InfoQualityPolicy enabledPolicy = CreatePolicy(requireEnabled: ["cpu"]);
        InfoQualityPolicy eventsPolicy = CreatePolicy(requireEvents: ["cpu"]);

        InfoQualityPolicyResult enabled = enabledPolicy.Evaluate(info);
        InfoQualityPolicyResult events = eventsPolicy.Evaluate(info);

        enabled.Failed.Should().BeFalse();
        enabled.Warnings.Should().BeEmpty();
        events.Failed.Should().BeTrue();
        AnalysisDiagnostic.FromWarning(events.Warnings.Single()).Code
            .Should().Be(AnalysisDiagnosticCodes.RequiredAnalysisEmpty);
    }

    [TestMethod]
    public void Evaluate_PositiveEvents_Passes()
    {
        TraceInfo info = CreateInfo(
            resolutionRate: 1.0,
            sampleCount: 10,
            "cpu",
                new AnalysisAvailability(FormatSupported: true, CaptureStatus.Enabled, 12));

        InfoQualityPolicyResult result = CreatePolicy(
            requireEnabled: ["cpu"],
            requireEvents: ["cpu"]).Evaluate(info);

        result.Failed.Should().BeFalse();
        result.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(CaptureStatus.Unknown, AnalysisDiagnosticCodes.CaptureStatusUnknown)]
    [DataRow(CaptureStatus.Disabled, AnalysisDiagnosticCodes.CaptureStatusDisabled)]
    public void Evaluate_UnavailableCaptureState_FailsWithDistinctCode(
        CaptureStatus status,
        string expectedCode)
    {
        TraceInfo info = CreateInfo(
            resolutionRate: 1.0,
            sampleCount: 10,
            "cpu",
            new AnalysisAvailability(FormatSupported: true, status, EventCount: null));

        InfoQualityPolicyResult result = CreatePolicy(requireEnabled: ["cpu"]).Evaluate(info);

        result.Failed.Should().BeTrue();
        AnalysisDiagnostic.FromWarning(result.Warnings.Single()).Code.Should().Be(expectedCode);
    }

    [TestMethod]
    public void Evaluate_UnsupportedAnalysis_FailsWithDistinctCode()
    {
        TraceInfo info = CreateInfo(
            resolutionRate: 1.0,
            sampleCount: 10,
            "alloc",
            new AnalysisAvailability(FormatSupported: false, CaptureStatus.Unknown, EventCount: null));

        InfoQualityPolicyResult result = CreatePolicy(requireEnabled: ["alloc"]).Evaluate(info);

        result.Failed.Should().BeTrue();
        AnalysisDiagnostic.FromWarning(result.Warnings.Single()).Code
            .Should().Be(AnalysisDiagnosticCodes.RequiredAnalysisUnsupported);
    }

    [TestMethod]
    public void Evaluate_StrictResolution_RequiresSamplesAndThreshold()
    {
        InfoQualityPolicy policy = CreatePolicy(strict: true);

        policy.Evaluate(CreateInfo(0.5, 10)).Failed.Should().BeTrue();
        policy.Evaluate(CreateInfo(SymbolGate.MinimumResolutionRate, 10)).Failed.Should().BeFalse();
        policy.Evaluate(CreateInfo(0.0, 0)).Failed.Should().BeFalse();
    }

    [TestMethod]
    public void TryCreate_UnknownAnalysis_IsUsageFailure()
    {
        bool created = InfoQualityPolicy.TryCreate(
            strict: false,
            requireEnabled: ["future"],
            requireEvents: null,
            out _,
            out string? error);

        created.Should().BeFalse();
        error.Should().Contain("--require-enabled").And.Contain("future").And.Contain("cpu");
    }

    [TestMethod]
    public void TryCreate_Duplicates_AreRemovedInCallerOrder()
    {
        InfoQualityPolicy.TryCreate(
            strict: false,
            requireEnabled: ["cpu", "alloc", "cpu"],
            requireEvents: ["exceptions", "exceptions"],
            out InfoQualityPolicy policy,
            out _).Should().BeTrue();

        policy.RequiredEnabled.Should().Equal("cpu", "alloc");
        policy.RequiredEvents.Should().Equal("exceptions");
    }

    private static InfoQualityPolicy CreatePolicy(
        bool strict = false,
        string[]? requireEnabled = null,
        string[]? requireEvents = null)
    {
        InfoQualityPolicy.TryCreate(
            strict,
            requireEnabled,
            requireEvents,
            out InfoQualityPolicy policy,
            out string? error).Should().BeTrue(error);

        return policy;
    }

    private static TraceInfo CreateInfo(
        double resolutionRate,
        int sampleCount,
        string? analysis = null,
        AnalysisAvailability? availability = null)
    {
        Dictionary<string, AnalysisAvailability> analyses = new(StringComparer.Ordinal);
        if (analysis is not null && availability is not null)
        {
            analyses[analysis] = availability;
        }

        return new TraceInfo(
            "trace.nettrace",
            TraceFormat.NetTrace,
            totalWeight: sampleCount,
            sampleCount,
            resolutionRate,
            threads: [],
            warnings: [],
            availableAnalyses: TraceCapabilities.AnalysesFor(TraceFormat.NetTrace),
            analyses);
    }
}
