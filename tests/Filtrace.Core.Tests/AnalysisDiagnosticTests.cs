// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Output;

[TestClass]
public sealed class AnalysisDiagnosticTests
{
    [TestMethod]
    public void FromWarning_ThinScope_CarriesCounts()
    {
        AnalysisDiagnostic diagnostic = AnalysisDiagnostic.FromWarning(
            "Only 32 periodic CPU records contribute to this method-level result; use at least 200 for directional confidence or capture longer.");

        diagnostic.Code.Should().Be(AnalysisDiagnosticCodes.ThinScope);
        diagnostic.Severity.Should().Be("warning");
        diagnostic.Data.Should().NotBeNull();
        diagnostic.Data!.ContributingRecords.Should().Be(32);
        diagnostic.Data.RecommendedMinimum.Should().Be(200);
    }

    [TestMethod]
    public void FromWarning_LowFrameResolution_CarriesPercentages()
    {
        AnalysisDiagnostic diagnostic = AnalysisDiagnostic.FromWarning(
            "Only 49% of frames resolved to a method name (< 80%); native frames may be unresolved.");

        diagnostic.Code.Should().Be(AnalysisDiagnosticCodes.LowFrameResolution);
        diagnostic.Data.Should().NotBeNull();
        diagnostic.Data!.ResolutionPercent.Should().Be(49);
        diagnostic.Data.MinimumResolutionPercent.Should().Be(80);
    }

    [TestMethod]
    [DataRow("PDB identity mismatch for module MyApp", AnalysisDiagnosticCodes.PdbIdentityMismatch)]
    [DataRow("Capture metadata 'trace.capture.json' could not be read: malformed. Provider enablement remains unknown where no events were observed.", AnalysisDiagnosticCodes.CaptureStatusUnknown)]
    [DataRow("The requested capture provider is disabled.", AnalysisDiagnosticCodes.CaptureStatusDisabled)]
    [DataRow("Required analysis 'alloc' is not supported by the Speedscope trace format.", AnalysisDiagnosticCodes.RequiredAnalysisUnsupported)]
    [DataRow("Required analysis 'cpu' recorded 0 events; at least 1 is required.", AnalysisDiagnosticCodes.RequiredAnalysisEmpty)]
    [DataRow("Selector matched multiple frame definitions.", AnalysisDiagnosticCodes.AmbiguousSelector)]
    [DataRow("Showing 25 rows; more would exceed the token budget.", AnalysisDiagnosticCodes.TruncatedOutput)]
    [DataRow("take 5000 exceeds the maximum; clamped to 1000.", AnalysisDiagnosticCodes.ClampedInput)]
    [DataRow("The time window was not applied to a speedscope profile.", AnalysisDiagnosticCodes.IgnoredScope)]
    [DataRow("manifest case failed to load", AnalysisDiagnosticCodes.CaseFailure)]
    public void FromWarning_KnownMessageFamily_AssignsStableCode(string message, string expectedCode)
    {
        AnalysisDiagnostic.FromWarning(message).Code.Should().Be(expectedCode);
    }

    [TestMethod]
    public void FromWarning_UnknownMessage_UsesGenericCode()
    {
        AnalysisDiagnostic diagnostic = AnalysisDiagnostic.FromWarning("Something worth inspecting happened.");

        diagnostic.Code.Should().Be(AnalysisDiagnosticCodes.Warning);
        diagnostic.Message.Should().Be("Something worth inspecting happened.");
        diagnostic.Data.Should().BeNull();
    }

    [TestMethod]
    public void FromWarning_NonManifestFailure_DoesNotClaimCaseFailure()
    {
        AnalysisDiagnostic diagnostic = AnalysisDiagnostic.FromWarning("Source mapping failed for this module.");

        diagnostic.Code.Should().Be(AnalysisDiagnosticCodes.Warning);
    }

    [TestMethod]
    public void FromWarning_AppliedScope_IsInformational()
    {
        AnalysisDiagnostic diagnostic = AnalysisDiagnostic.FromWarning(
            "Scoped to the 'HotLoopBench' process tree; pass --all-processes to read every process.");

        diagnostic.Code.Should().Be(AnalysisDiagnosticCodes.ScopeApplied);
        diagnostic.Severity.Should().Be("info");
    }

    [TestMethod]
    public void FromWarning_OversizedNumericData_FallsBackWithoutThrowing()
    {
        string message =
            $"Only {new string('9', 100)} periodic CPU records contribute to this method-level result; "
            + "use at least 200 for directional confidence or capture longer.";

        AnalysisDiagnostic diagnostic = AnalysisDiagnostic.FromWarning(message);

        diagnostic.Code.Should().Be(AnalysisDiagnosticCodes.Warning);
        diagnostic.Data.Should().BeNull();
    }
}
