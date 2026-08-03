// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.JIT;
using TraceLog = Microsoft.Diagnostics.Tracing.Etlx.TraceLog;
using TraceLogEventSource = Microsoft.Diagnostics.Tracing.Etlx.TraceLogEventSource;
using TraceLogOptions = Microsoft.Diagnostics.Tracing.Etlx.TraceLogOptions;
using TraceProcess = Microsoft.Diagnostics.Tracing.Analysis.TraceProcess;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The JIT-stats provider: reads the structured just-in-time compilation records
///  from a .NET EventPipe trace into a <see cref="JitStatsResult"/>.
/// </summary>
/// <remarks>
///  <para>
///   JIT activity is captured by the runtime's method events (a JIT EventPipe
///   profile), which TraceEvent's analysis layer assembles into per-method
///   <c>TraceJittedMethod</c> records. Unlike the stack-source families this is
///   structured data, not weighted stacks, so it returns its own result rather
///   than a <see cref="StackSampleSource"/>.
///  </para>
/// </remarks>
public sealed class JitStatsProvider
{
    // The JSON scaffolding around one method record - the property names, the
    // punctuation, and the numeric IL size, native size, and compile-time fields -
    // which the per-record estimate adds to the record's variable text.
    private const int RecordScaffoldTokens = 32;

    /// <summary>
    ///  Reads the JIT-stats report from the EventPipe trace at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.nettrace</c> file path.</param>
    /// <returns>The JIT report, or an empty report when the trace carries no JIT events.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public JitStatsResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        using TraceLog traceLog = TraceConverter.OpenTraceLog(fullPath, out _);

        // The JIT analysis layer reconstructs per-method records from the raw method
        // events as the source is processed; request it before draining the events.
        using TraceLogEventSource source = traceLog.Events.GetSource();
        source.NeedLoadedDotNetRuntimes();
        source.Process();

        List<JitMethodRecord> records = [];
        foreach (TraceProcess process in source.Processes())
        {
            TraceLoadedDotNetRuntime? runtime = process.LoadedDotNetRuntime();
            if (runtime is null)
            {
                continue;
            }

            foreach (TraceJittedMethod method in runtime.JIT.Methods)
            {
                records.Add(new JitMethodRecord(
                    method.MethodName ?? string.Empty,
                    method.ModuleILPath ?? string.Empty,
                    method.ILSize,
                    method.NativeSize,
                    method.CompileCpuTimeMSec,
                    method.OptimizationTier.ToString()));
            }
        }

        return Summarize(records);
    }

    /// <summary>
    ///  Limits a report's per-method detail to the costliest compiles that fit both
    ///  <paramref name="top"/> and <see cref="OutputBudget.DefaultRowBudgetTokens"/>,
    ///  leaving the aggregate summary untouched.
    /// </summary>
    /// <param name="report">The full report, as returned by <see cref="Read"/>.</param>
    /// <param name="top">The caller's maximum detail row count. Must be positive.</param>
    /// <param name="warning">
    ///  The warning naming what was dropped, or <see langword="null"/> when the whole
    ///  detail list was kept.
    /// </param>
    /// <returns>
    ///  The limited report, or <paramref name="report"/> itself when every method fit.
    /// </returns>
    /// <remarks>
    ///  <para>
    ///   Shared by both heads so they bound and word the result identically. A report that
    ///   fits comes back untouched, in the trace order <see cref="Read"/> produced; only a
    ///   report that has to drop methods is reordered, so that the costliest compiles are
    ///   the ones kept. Asking the committed 840-method JIT fixture for every method
    ///   measured roughly 79,000 estimated tokens before this bound existed, three times
    ///   the ceiling, and a startup trace jits far more than that.
    ///  </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is not positive.</exception>
    public static JitStatsResult LimitDetail(JitStatsResult report, int top, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfNegative(top);

        List<JitMethodRecord> kept = OutputBudget.TakeWithinBudget(
            report.Methods.OrderByDescending(static method => method.CompileMs).Take(top),
            EstimateRecordTokens,
            OutputBudget.DefaultRowBudgetTokens,
            out bool budgetTruncated);

        if (kept.Count == report.Methods.Count)
        {
            warning = null;
            return report;
        }

        warning = budgetTruncated
            ? $"Showing {kept.Count} of {report.MethodCount} methods by compile time; more would exceed the "
                + $"{OutputBudget.DefaultRowBudgetTokens}-token detail budget that holds the whole response under "
                + $"the {OutputBudget.DefaultCeilingTokens}-token ceiling. The aggregate summary still covers "
                + "every method."
            : top == 0
                ? $"Aggregate only: {report.MethodCount} methods were not listed. Ask again with a positive top "
                    + "for the per-method detail."
                : $"Showing the top {top} of {report.MethodCount} methods by compile time.";

        return report with { Methods = kept };
    }

    private static int EstimateRecordTokens(JitMethodRecord method) =>
        RecordScaffoldTokens
        + OutputBudget.EstimateTokens(method.MethodName)
        + OutputBudget.EstimateTokens(method.ModuleILPath)
        + OutputBudget.EstimateTokens(method.OptimizationTier);

    private static JitStatsResult Summarize(List<JitMethodRecord> records)
    {
        if (records.Count == 0)
        {
            return new JitStatsResult(0, 0.0, 0.0, 0.0, 0, 0, records);
        }

        double totalCompile = 0.0;
        double maxCompile = 0.0;
        long totalIL = 0;
        long totalNative = 0;

        foreach (JitMethodRecord method in records)
        {
            totalCompile += method.CompileMs;
            maxCompile = Math.Max(maxCompile, method.CompileMs);
            totalIL += method.ILSize;
            totalNative += method.NativeSize;
        }

        return new JitStatsResult(
            records.Count,
            totalCompile,
            maxCompile,
            totalCompile / records.Count,
            totalIL,
            totalNative,
            records);
    }
}
