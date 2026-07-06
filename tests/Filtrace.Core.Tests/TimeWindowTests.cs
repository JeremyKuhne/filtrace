// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class TimeWindowTests
{
    [TestMethod]
    public void Contains_BothBounds_IsInclusiveOnEachEnd()
    {
        TimeWindow window = new(1000.0, 5000.0);

        window.Contains(1000.0).Should().BeTrue("the lower bound is inclusive");
        window.Contains(3000.0).Should().BeTrue();
        window.Contains(5000.0).Should().BeTrue("the upper bound is inclusive");

        window.Contains(999.999).Should().BeFalse();
        window.Contains(5000.001).Should().BeFalse();
    }

    [TestMethod]
    public void Contains_OpenStart_RunsFromTheTraceStart()
    {
        TimeWindow window = new(null, 5000.0);

        window.Contains(0.0).Should().BeTrue();
        window.Contains(5000.0).Should().BeTrue();
        window.Contains(5000.001).Should().BeFalse();
    }

    [TestMethod]
    public void Contains_OpenEnd_RunsToTheTraceEnd()
    {
        TimeWindow window = new(1000.0, null);

        window.Contains(999.999).Should().BeFalse();
        window.Contains(1000.0).Should().BeTrue();
        window.Contains(double.MaxValue).Should().BeTrue();
    }

    [TestMethod]
    public void Contains_Unbounded_KeepsEverything()
    {
        TimeWindow window = new(null, null);

        window.IsBounded.Should().BeFalse();
        window.Contains(0.0).Should().BeTrue();
        window.Contains(1_000_000.0).Should().BeTrue();
    }

    [TestMethod]
    public void IsBounded_ReflectsWhetherEitherBoundIsSet()
    {
        new TimeWindow(1000.0, 5000.0).IsBounded.Should().BeTrue();
        new TimeWindow(1000.0, null).IsBounded.Should().BeTrue();
        new TimeWindow(null, 5000.0).IsBounded.Should().BeTrue();
        new TimeWindow(null, null).IsBounded.Should().BeFalse();
    }

    [TestMethod]
    public void ToString_RendersOpenBoundsAsWords()
    {
        new TimeWindow(1000.0, 5000.0).ToString().Should().Be("[1000, 5000] ms");
        new TimeWindow(1000.0, null).ToString().Should().Be("[1000, end] ms");
        new TimeWindow(null, 5000.0).ToString().Should().Be("[start, 5000] ms");
        new TimeWindow(null, null).ToString().Should().Be("[start, end] ms");

        // A fractional millisecond keeps up to three decimals and no locale grouping.
        new TimeWindow(1000.5, 5000.25).ToString().Should().Be("[1000.5, 5000.25] ms");
    }

    [TestMethod]
    public void Constructor_RejectsAnInvertedOrNegativeOrNaNBound()
    {
        Action inverted = () => _ = new TimeWindow(5000.0, 1000.0);
        inverted.Should().Throw<ArgumentException>();

        Action negativeStart = () => _ = new TimeWindow(-1.0, 5000.0);
        negativeStart.Should().Throw<ArgumentOutOfRangeException>();

        Action negativeEnd = () => _ = new TimeWindow(1000.0, -1.0);
        negativeEnd.Should().Throw<ArgumentOutOfRangeException>();

        Action nan = () => _ = new TimeWindow(double.NaN, 5000.0);
        nan.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void TryParse_EmptyOrNull_IsAWindowlessSuccess()
    {
        TimeWindow.TryParse(null, out double? start, out double? end, out string? error).Should().BeTrue();
        start.Should().BeNull();
        end.Should().BeNull();
        error.Should().BeNull();

        TimeWindow.TryParse("", out start, out end, out error).Should().BeTrue();
        start.Should().BeNull();
        end.Should().BeNull();
    }

    [TestMethod]
    public void TryParse_BothBounds_ParsesEach()
    {
        TimeWindow.TryParse("1000,5000", out double? start, out double? end, out string? error).Should().BeTrue();
        start.Should().Be(1000.0);
        end.Should().Be(5000.0);
        error.Should().BeNull();
    }

    [TestMethod]
    public void TryParse_OpenBounds_LeaveTheOmittedSideNull()
    {
        TimeWindow.TryParse("1000,", out double? start, out double? end, out _).Should().BeTrue();
        start.Should().Be(1000.0);
        end.Should().BeNull();

        TimeWindow.TryParse(",5000", out start, out end, out _).Should().BeTrue();
        start.Should().BeNull();
        end.Should().Be(5000.0);
    }

    [TestMethod]
    public void TryParse_TrimsWhitespaceAndParsesInvariantly()
    {
        TimeWindow.TryParse(" 1000.5 , 5000.25 ", out double? start, out double? end, out _).Should().BeTrue();
        start.Should().Be(1000.5);
        end.Should().Be(5000.25);
    }

    [TestMethod]
    public void TryParse_Malformed_FailsWithAMessage()
    {
        // No comma at all.
        TimeWindow.TryParse("1000", out _, out _, out string? error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();

        // More than one comma.
        TimeWindow.TryParse("1000,2000,3000", out _, out _, out error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();

        // Neither bound given.
        TimeWindow.TryParse(",", out _, out _, out error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();

        // A non-numeric bound.
        TimeWindow.TryParse("abc,5000", out _, out _, out error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();

        // A negative bound.
        TimeWindow.TryParse("-1,5000", out _, out _, out error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();

        // Start greater than end.
        TimeWindow.TryParse("5000,1000", out _, out _, out error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }
}
