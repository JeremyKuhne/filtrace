// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Filtrace.Output;

namespace Filtrace.Tracing.Providers;

[TestClass]
public sealed class JitStatsProviderTests
{
    private static string FixturePath(string name) =>
        Path.Join(AppContext.BaseDirectory, "Fixtures", name);

    // The JIT smoke trace is captured under the JIT profile, so it carries the
    // method-jitting events this provider reads.
    private static JitStatsResult LoadJitStats() =>
        new JitStatsProvider().Read(FixturePath("jit.nettrace"));

    [TestMethod]
    public void Read_JitFixture_ReportsCompiledMethods()
    {
        JitStatsResult result = LoadJitStats();

        result.MethodCount.Should().BeGreaterThan(0, "the JIT workload compiles methods on first call");
        result.Methods.Should().HaveCount(result.MethodCount);
    }

    [TestMethod]
    public void Read_JitFixture_CompileSummaryIsConsistent()
    {
        JitStatsResult result = LoadJitStats();

        result.TotalCompileMs.Should().BeGreaterThan(0.0);
        result.MaxCompileMs.Should().BeGreaterThan(0.0);
        // The mean lies on the total/count line, and the max never exceeds the total.
        result.MeanCompileMs.Should().BeApproximately(result.TotalCompileMs / result.MethodCount, 0.001);
        result.MaxCompileMs.Should().BeLessThanOrEqualTo(result.TotalCompileMs);
    }

    [TestMethod]
    public void Read_JitFixture_SizeTotalsMatchTheRecords()
    {
        JitStatsResult result = LoadJitStats();

        result.TotalILSize.Should().Be(result.Methods.Sum(static m => (long)m.ILSize));
        result.TotalNativeSize.Should().Be(result.Methods.Sum(static m => (long)m.NativeSize));
    }

    [TestMethod]
    public void Read_JitFixture_EveryRecordIsWellFormed()
    {
        JitStatsResult result = LoadJitStats();

        result.Methods.Should().OnlyContain(m => m.MethodName.Length > 0);
        result.Methods.Should().OnlyContain(m => m.ILSize >= 0 && m.NativeSize >= 0);
        result.Methods.Should().OnlyContain(m => m.CompileMs >= 0.0);
    }

    [TestMethod]
    public void Read_JitFixture_IncludesTheBenchmarkMethods()
    {
        JitStatsResult result = LoadJitStats();

        // The JitLoop benchmark's deliberately named methods are jitted once each.
        result.Methods.Should().Contain(m => m.MethodName.Contains("JitMethod", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LimitDetail_TopLargerThanTheBudget_ClampsTheSerializedResponse()
    {
        // The detail row count is caller-supplied and multiplies straight into response
        // size. Before the list was bounded by tokens, asking this 840-method fixture for
        // every method serialized to about 79,000 estimated tokens - three times the
        // ceiling the output budget documents.
        JitStatsResult full = LoadJitStats();

        JitStatsResult limited = JitStatsProvider.LimitDetail(full, top: 100_000, out string? warning);

        limited.Methods.Count.Should().BeLessThan(full.Methods.Count);
        limited.MethodCount.Should().Be(full.MethodCount, "the aggregate summary still covers every method");
        warning.Should().NotBeNull()
            .And.Contain(OutputBudget.DefaultRowBudgetTokens.ToString(CultureInfo.InvariantCulture));

        string serialized = OutputJson.Serialize(new AnalysisResult<JitStatsResult>(limited));
        OutputBudget.EstimateTokens(serialized).Should().BeLessThan(OutputBudget.DefaultCeilingTokens);
    }

    [TestMethod]
    public void LimitDetail_TopBelowTheMethodCount_KeepsTheCostliestCompiles()
    {
        JitStatsResult full = LoadJitStats();

        JitStatsResult limited = JitStatsProvider.LimitDetail(full, top: 3, out string? warning);

        limited.Methods.Should().HaveCount(3).And.BeInDescendingOrder(static method => method.CompileMs);
        limited.Methods[0].CompileMs.Should().Be(full.MaxCompileMs);
        warning.Should().Contain("Showing the top 3");
    }

    [TestMethod]
    public void LimitDetail_EverythingFits_ReturnsTheReportUnchanged()
    {
        // A report that fits keeps the trace order Read produced, and reports no warning.
        JitMethodRecord[] methods =
        [
            new("Slow.Method", "slow.dll", 10, 20, 5.0, "Optimized"),
            new("Fast.Method", "fast.dll", 30, 40, 1.0, "QuickJitted")
        ];

        JitStatsResult report = new(2, 6.0, 5.0, 3.0, 40, 60, methods);

        JitStatsResult limited = JitStatsProvider.LimitDetail(report, top: 25, out string? warning);

        limited.Should().BeSameAs(report);
        warning.Should().BeNull();
    }

    [TestMethod]
    public void LimitDetail_ZeroTopOnAnEmptyReport_ReportsNothingToDrop()
    {
        // Nothing was withheld, so the "Aggregate only" warning would be misleading:
        // the early return covers this before the top 0 wording is reached.
        JitStatsResult empty = new(0, 0.0, 0.0, 0.0, 0, 0, []);

        JitStatsResult limited = JitStatsProvider.LimitDetail(empty, top: 0, out string? warning);

        limited.Should().BeSameAs(empty);
        warning.Should().BeNull();
    }

    [TestMethod]
    public void LimitDetail_NegativeTop_Throws()
    {
        JitStatsResult report = new(0, 0.0, 0.0, 0.0, 0, 0, []);

        Action act = () => JitStatsProvider.LimitDetail(report, top: -1, out _);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void LimitDetail_ZeroTop_KeepsTheAggregateAndDropsEveryRow()
    {
        // Zero is how an agent asks for the summary alone; it is a point on the row
        // axis, not an error.
        JitStatsResult full = LoadJitStats();

        JitStatsResult limited = JitStatsProvider.LimitDetail(full, top: 0, out string? warning);

        limited.Methods.Should().BeEmpty();
        limited.MethodCount.Should().Be(full.MethodCount);
        warning.Should().Contain("Aggregate only");
    }

    [TestMethod]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        JitStatsProvider provider = new();

        Action act = () => provider.Read(FixturePath("does-not-exist.nettrace"));

        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(stringArrayData: null)]
    public void Read_NullOrEmptyPath_ThrowsArgument(string? path)
    {
        JitStatsProvider provider = new();

        Action act = () => provider.Read(path!);

        act.Should().Throw<ArgumentException>();
    }
}
