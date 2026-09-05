// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class CaptureManifestScopeTests
{
    [TestMethod]
    public void GetCase_ExactId_ReturnsCase()
    {
        CaptureManifestCase captureCase = CaseWithInvocations(4242);
        CaptureManifest manifest = Manifest(captureCase, process: null);

        manifest.GetCase("case").Should().BeSameAs(captureCase);
    }

    [TestMethod]
    public void GetCase_MissingId_ThrowsInvalidData()
    {
        CaptureManifest manifest = Manifest(CaseWithInvocations(), process: null);

        Action act = () => manifest.GetCase("missing");

        act.Should().Throw<InvalidDataException>().WithMessage("*no case with id 'missing'*");
    }

    [TestMethod]
    public void GetCase_OverlongId_ThrowsArgument()
    {
        CaptureManifest manifest = Manifest(CaseWithInvocations(), process: null);
        string caseId = new('x', CaptureManifestReader.MaxCaseIdLength + 1);

        Action act = () => manifest.GetCase(caseId);

        act.Should().Throw<ArgumentException>().WithParameterName("caseId");
    }

    [TestMethod]
    public void GetCase_ControlCharacterInId_ThrowsArgument()
    {
        CaptureManifest manifest = Manifest(CaseWithInvocations(), process: null);

        Action act = () => manifest.GetCase("case\nid");

        act.Should().Throw<ArgumentException>()
            .WithParameterName("caseId")
            .WithMessage("*non-control characters*");
    }

    [TestMethod]
    public void GetCase_WhitespaceIdAcceptedByReader_ReturnsCase()
    {
        CaptureManifestCase captureCase = CaseWithInvocations() with { Id = " " };
        CaptureManifest manifest = Manifest(captureCase, process: null);

        manifest.GetCase(" ").Should().BeSameAs(captureCase);
    }

    [TestMethod]
    public void ResolveCaseScope_RecordedInvocations_UsesExactProcessIds()
    {
        CaptureManifestCase captureCase = CaseWithInvocations(4242, 17, 4242);
        CaptureManifest manifest = Manifest(captureCase, process: "dotnet");

        ScopeRequest? scope = manifest.ResolveCaseScope(captureCase, requested: null);

        scope.Should().NotBeNull();
        ProcessIdSelector selector = scope!.Selector.Should().BeOfType<ProcessIdSelector>().Which;
        selector.ProcessIds.Should().Equal(17, 4242);
        scope.IncludeChildren.Should().BeTrue();
    }

    [TestMethod]
    public void ResolveCaseScope_ExplicitProcessOverride_Wins()
    {
        CaptureManifestCase captureCase = CaseWithInvocations(4242);
        CaptureManifest manifest = Manifest(captureCase, process: "recorded");
        ScopeRequest requested = ScopeRequest.ForProcess("explicit", includeChildren: false);

        ScopeRequest? scope = manifest.ResolveCaseScope(captureCase, requested);

        scope.Should().BeSameAs(requested);
    }

    [TestMethod]
    public void ResolveCaseScope_AllProcessesOverride_Wins()
    {
        CaptureManifestCase captureCase = CaseWithInvocations(4242);
        CaptureManifest manifest = Manifest(captureCase, process: "recorded");

        ScopeRequest? scope = manifest.ResolveCaseScope(captureCase, ScopeRequest.AllProcesses);

        scope.Should().BeSameAs(ScopeRequest.AllProcesses);
    }

    [TestMethod]
    public void ResolveCaseScope_AutomaticRefinements_SurviveExactIdFallback()
    {
        CaptureManifestCase captureCase = CaseWithInvocations(4242);
        CaptureManifest manifest = Manifest(captureCase, process: "recorded");
        ScopeRequest requested = ScopeRequest.AutoScope(includeChildren: false)
            .WithActivity("request")
            .WithTimeWindow(12.5, 20.0);

        ScopeRequest? scope = manifest.ResolveCaseScope(captureCase, requested);

        scope.Should().NotBeNull();
        scope!.Selector.Should().BeOfType<ProcessIdSelector>();
        scope.IncludeChildren.Should().BeFalse();
        scope.ActivityName.Should().Be("request");
        scope.Window.Should().NotBeNull();
        scope.Window!.Value.StartMSec.Should().Be(12.5);
        scope.Window.Value.EndMSec.Should().Be(20.0);
    }

    [TestMethod]
    public void ResolveCaseScope_LegacyCase_FallsBackToManifestProcess()
    {
        CaptureManifestCase captureCase = CaseWithInvocations();
        CaptureManifest manifest = Manifest(captureCase, process: "HotLoop");

        ScopeRequest? scope = manifest.ResolveCaseScope(captureCase, ScopeRequest.AutoScope(includeChildren: false));

        scope.Should().NotBeNull();
        ProcessNameSelector selector = scope!.Selector.Should().BeOfType<ProcessNameSelector>().Which;
        selector.NameSubstring.Should().Be("HotLoop");
        scope.IncludeChildren.Should().BeFalse();
    }

    [TestMethod]
    public void ResolveCaseScope_NoRecordedScope_PreservesRequestedAutomaticScope()
    {
        CaptureManifestCase captureCase = CaseWithInvocations();
        CaptureManifest manifest = Manifest(captureCase, process: null);
        ScopeRequest requested = ScopeRequest.AutoScope(includeChildren: false);

        ScopeRequest? scope = manifest.ResolveCaseScope(captureCase, requested);

        scope.Should().BeSameAs(requested);
    }

    [TestMethod]
    public void ResolveCaseScope_NoRecordedOrRequestedScope_ReturnsNull()
    {
        CaptureManifestCase captureCase = CaseWithInvocations();
        CaptureManifest manifest = Manifest(captureCase, process: null);

        manifest.ResolveCaseScope(captureCase, requested: null).Should().BeNull();
    }

    private static CaptureManifest Manifest(CaptureManifestCase captureCase, string? process) =>
        new("manifest.json", process, [captureCase]);

    private static CaptureManifestCase CaseWithInvocations(params int[] processIds) =>
        new("case", Benchmark: null, string.Empty, "Case", "trace.etl", SymbolsDirectory: null, OperationCount: null, OperationUnit: null)
        {
            Invocations =
            [
                .. processIds.Select(static (processId, index) => new CaptureInvocation(
                    index + 1,
                    processId,
                    0,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch))
            ]
        };
}
