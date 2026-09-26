// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Core.Tests;

[TestClass]
public sealed class DiffProcessScopesTests
{
    [TestMethod]
    public void TryResolve_WithoutPerArmIds_PreservesSharedScope()
    {
        ScopeRequest shared = ScopeRequest.ForProcessIds([9144], includeChildren: false);

        bool resolved = DiffProcessScopes.TryResolve(
            shared, beforePid: null, afterPid: null, includeChildren: false,
            out DiffProcessScopes scopes, out string? error);

        resolved.Should().BeTrue();
        error.Should().BeNull();
        scopes.HasPerArmIds.Should().BeFalse();
        scopes.Before.Should().BeSameAs(shared);
        scopes.After.Should().BeSameAs(shared);
    }

    [TestMethod]
    public void TryResolve_PerArmIds_PreservesBothArraysAndChildren()
    {
        bool resolved = DiffProcessScopes.TryResolve(
            ScopeRequest.Auto, beforePid: [9144, 40356], afterPid: [40356], includeChildren: false,
            out DiffProcessScopes scopes, out string? error);

        resolved.Should().BeTrue();
        error.Should().BeNull();
        scopes.HasPerArmIds.Should().BeTrue();
        ((ProcessIdSelector)scopes.Before!.Selector!).ProcessIds.Should().Equal(9144, 40356);
        ((ProcessIdSelector)scopes.After!.Selector!).ProcessIds.Should().Equal(40356);
        scopes.Before.IncludeChildren.Should().BeFalse();
        scopes.After.IncludeChildren.Should().BeFalse();
    }

    [TestMethod]
    public void TryResolve_IncompleteConflictingOrInvalidIds_ExplainsError()
    {
        (ScopeRequest? Shared, int[]? Before, int[]? After, string Error)[] cases =
        [
            (ScopeRequest.Auto, [9144], null, "both beforePid and afterPid"),
            (ScopeRequest.Auto, null, [40356], "both beforePid and afterPid"),
            (ScopeRequest.ForProcess("compiler"), [9144], [40356], "cannot be combined"),
            (ScopeRequest.ForProcessIds([9144]), [9144], [40356], "cannot be combined"),
            (ScopeRequest.AllProcesses, [9144], [40356], "cannot be combined"),
            (ScopeRequest.Auto, [], [40356], "at least one process id"),
            (ScopeRequest.Auto, [9144], [], "at least one process id"),
            (ScopeRequest.Auto, [0], [40356], "beforePid 0"),
            (ScopeRequest.Auto, [9144], [-1], "afterPid -1")
        ];

        foreach ((ScopeRequest? shared, int[]? before, int[]? after, string expected) in cases)
        {
            bool resolved = DiffProcessScopes.TryResolve(
                shared, before, after, includeChildren: true, out _, out string? error);

            resolved.Should().BeFalse();
            error.Should().Contain(expected);
        }
    }

    [TestMethod]
    public void TraceError_RejectsMissingIdOrUnsupportedFormat_WithoutDiscardingValidThinScopes()
    {
        DiffProcessScopes.TryResolve(
            ScopeRequest.Auto, [9144], [40356], includeChildren: false,
            out DiffProcessScopes scopes, out _).Should().BeTrue();

        TraceInfo before = Info(TraceFormat.Etl, [9144], [9144]);
        TraceInfo missingAfter = Info(TraceFormat.Etl, [40356], []);
        TraceInfo thinAfter = Info(TraceFormat.Etl, [40356], [40356]);
        TraceInfo noProcessAxis = Info(TraceFormat.NetTrace, [], []);
        TraceInfo missingScope = new("trace.etl", TraceFormat.Etl, 0, 0, 1, [], [], []);

        scopes.TraceError(before, before: true).Should().BeNull();
        scopes.TraceError(missingAfter, before: false).Should().Contain("Current process id 40356 was not found");
        scopes.TraceError(thinAfter, before: false).Should().BeNull();
        scopes.TraceError(noProcessAxis, before: true).Should().Contain("requires an .etl trace");
        scopes.TraceError(missingScope, before: false).Should().Contain("was not applied to this .etl trace");
    }

    private static TraceInfo Info(TraceFormat format, int[] requested, int[] matched) =>
        new("trace", format, 0, 0, 1, [], [], [])
        {
            AppliedProcessScope = new("ids", Process: null, requested, matched, [], IncludeChildren: false)
        };
}
