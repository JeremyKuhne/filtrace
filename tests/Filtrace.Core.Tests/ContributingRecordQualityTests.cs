// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Core.Tests;

[TestClass]
public sealed class ContributingRecordQualityTests
{
    [TestMethod]
    public void TryGetMethodWarning_ThinPeriodicCpuScope_Warns()
    {
        bool warned = ContributingRecordQuality.TryGetMethodWarning(
            StackRecordSemantics.PeriodicCpuSamples,
            32,
            out string? warning);

        warned.Should().BeTrue();
        warning.Should().Contain("32").And.Contain("200");
    }

    [TestMethod]
    public void TryGetMethodWarning_EventedIntervals_DoesNotApplyPeriodicThreshold()
    {
        bool warned = ContributingRecordQuality.TryGetMethodWarning(
            StackRecordSemantics.EventedIntervals,
            4,
            out string? warning);

        warned.Should().BeFalse();
        warning.Should().BeNull();
    }

    [TestMethod]
    public void TryGetMethodWarning_EmptyPeriodicScope_LeavesEmptyResultWarningToCaller()
    {
        bool warned = ContributingRecordQuality.TryGetMethodWarning(
            StackRecordSemantics.PeriodicCpuSamples,
            0,
            out string? warning);

        warned.Should().BeFalse();
        warning.Should().BeNull();
    }

    [TestMethod]
    public void TryGetLineWarning_ThinPeriodicCpuScope_UsesLineThreshold()
    {
        bool warned = ContributingRecordQuality.TryGetLineWarning(
            StackRecordSemantics.PeriodicCpuSamples,
            32,
            out string? warning);

        warned.Should().BeTrue();
        warning.Should().Contain("32").And.Contain("1000");
    }
}