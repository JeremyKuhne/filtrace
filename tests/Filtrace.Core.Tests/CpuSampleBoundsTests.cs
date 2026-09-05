// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class CpuSampleBoundsTests
{
    [TestMethod]
    public void Resolve_WithinBounds_IsNotClamped()
    {
        // 1 ms is the ETW default and inside every platform's honored range.
        CpuSampleInterval interval = CpuSampleBounds.Resolve(1.0);

        interval.RequestedMSec.Should().Be(1.0);
        interval.EffectiveMSec.Should().Be(1.0);
        interval.Clamped.Should().BeFalse();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Resolve_BelowTheFloor_ClampsUpAndReportsIt()
    {
        if (!CpuSampleBounds.TryReadTimerBounds(out double minimumMSec, out _))
        {
            Assert.Inconclusive("This platform does not report profile source bounds.");
        }

        // Half the floor is a rate the OS accepts and echoes back but does not deliver.
        double belowFloor = minimumMSec / 2.0;
        CpuSampleInterval interval = CpuSampleBounds.Resolve(belowFloor);

        interval.RequestedMSec.Should().Be(belowFloor);
        interval.EffectiveMSec.Should().Be(minimumMSec);
        interval.Clamped.Should().BeTrue();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Resolve_AboveTheCeiling_ClampsDownAndReportsIt()
    {
        if (!CpuSampleBounds.TryReadTimerBounds(out _, out double maximumMSec))
        {
            Assert.Inconclusive("This platform does not report profile source bounds.");
        }

        CpuSampleInterval interval = CpuSampleBounds.Resolve(maximumMSec * 2.0);

        interval.EffectiveMSec.Should().Be(maximumMSec);
        interval.Clamped.Should().BeTrue();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void TryReadTimerBounds_ReportsASubMillisecondFloor()
    {
        bool read = CpuSampleBounds.TryReadTimerBounds(out double minimumMSec, out double maximumMSec);

        read.Should().BeTrue("Windows reports the interval timer's honored range");

        // Measured 0.1221 ms on Windows 11 (10.0.26200); the assertion is deliberately
        // loose because the floor is a platform property, not a filtrace constant. What
        // matters is that it is below 1 ms - which is what made the old range wrong.
        minimumMSec.Should().BeGreaterThan(0.0).And.BeLessThan(1.0);
        maximumMSec.Should().BeGreaterThan(minimumMSec);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void TryReadTimerBounds_NeedsNoElevation()
    {
        // The bounds are queried to validate a request before a capture is attempted, so
        // they have to be readable in the non-elevated path that reports the error.
        CpuSampleBounds.TryReadTimerBounds(out _, out _).Should().BeTrue();
    }

    [TestMethod]
    public void Resolve_NonPositiveOrNonFinite_Throws()
    {
        foreach (double invalid in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Action resolve = () => CpuSampleBounds.Resolve(invalid);
            resolve.Should().Throw<ArgumentOutOfRangeException>($"{invalid} is not a sample interval");
        }
    }

    [TestMethod]
    public void AcceptedRange_BracketsTheHonoredRange()
    {
        // The attribute bounds are an outer sanity check; the honored range is read from
        // the machine. If the accepted range were ever narrower, the CLI would reject an
        // interval the platform supports - which is the defect SC6 fixed.
        if (!CpuSampleBounds.TryReadTimerBounds(out double minimumMSec, out double maximumMSec))
        {
            Assert.Inconclusive("This platform does not report profile source bounds.");
        }

        CpuSampleBounds.MinimumAcceptedMSec.Should().BeLessThanOrEqualTo(minimumMSec);
        CpuSampleBounds.MaximumAcceptedMSec.Should().BeGreaterThanOrEqualTo(maximumMSec);
    }
}
