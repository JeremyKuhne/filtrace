// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Core.Tests;

[TestClass]
public sealed class NullabilityContractTests
{
    [TestMethod]
    [DataRow(null, "request", false)]
    [DataRow(0.0, "request", false)]
    [DataRow(2.0, null, false)]
    [DataRow(2.0, "request", true)]
    [DataRow(2.0, "", true)]
    public void HasCompleteOperationMetadata_ReflectsCountAndUnit(
        double? count,
        string? unit,
        bool expected)
    {
        CaptureManifestCase captureCase = new(
            "case", "Benchmark", "", "Benchmark", "trace.etl", SymbolsDirectory: null, count, unit);

        captureCase.HasCompleteOperationMetadata.Should().Be(expected);
        if (captureCase.HasCompleteOperationMetadata)
        {
            captureCase.OperationCount.Value.Should().BeGreaterThan(0);
            string operationUnit = captureCase.OperationUnit;
            operationUnit.Should().Be(unit);
        }
    }

    [TestMethod]
    public void TryGetMethodWarning_TrueReturn_AllowsNonNullableConsumer()
    {
        if (!ContributingRecordQuality.TryGetMethodWarning(
            StackRecordSemantics.PeriodicCpuSamples, 32, out string? warning))
        {
            Assert.Fail("A thin periodic CPU scope must produce a warning.");
        }

        string message = warning;
        message.Length.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void TryGetLineWarning_TrueReturn_AllowsNonNullableConsumer()
    {
        if (!ContributingRecordQuality.TryGetLineWarning(
            StackRecordSemantics.PeriodicCpuSamples, 32, out string? warning))
        {
            Assert.Fail("A thin source-line scope must produce a warning.");
        }

        string message = warning;
        message.Length.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void TryResolveLanes_FalseReturn_AllowsNonNullableErrorConsumer()
    {
        if (TimelineProvider.TryResolveLanes("unknown", out _, out string? error))
        {
            Assert.Fail("An unknown lane must be rejected.");
        }

        string message = error;
        message.Should().Contain("Unknown lane");
    }

    [TestMethod]
    public void HasPerArmIds_TrueValue_ProvesBothScopesArePresent()
    {
        DiffProcessScopes.TryResolve(
            shared: null, beforePid: [1], afterPid: [2], includeChildren: false,
            out DiffProcessScopes scopes, out _).Should().BeTrue();

        if (!scopes.HasPerArmIds)
        {
            Assert.Fail("Exact per-arm process ids must establish both scopes.");
        }

        ScopeRequest before = scopes.Before;
        ScopeRequest after = scopes.After;
        before.Selector.Should().BeOfType<ProcessIdSelector>();
        after.Selector.Should().BeOfType<ProcessIdSelector>();
    }
}
