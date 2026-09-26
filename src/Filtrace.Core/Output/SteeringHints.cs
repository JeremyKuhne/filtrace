// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Output;

/// <summary>
///  The steering-hint taxonomy: the canonical next-step nudges a verb attaches to
///  its <see cref="AnalysisResult{T}"/> so an agent mid-investigation is pointed
///  at the smallest useful follow-up rather than left to guess.
/// </summary>
/// <remarks>
///  <para>
///   The output contract reserves a hints channel; this is what fills it for the
///   ranking-family verbs. Each helper turns a verb's result into the next
///   evidence-backed action: an unresolved CPU leader checks frame-name quality
///   before an optional resolved drill, a non-CPU or windowed ranking stays in
///   the same metric/scope, and resolved callers and diffs suggest the next
///   frame to inspect. The nudges name an operation only when its scope can be
///   preserved.
///  </para>
///  <para>
///   The text hints remain advisory messages for source and CLI compatibility. The
///   returned list also carries operation-neutral metadata for a complete follow-up;
///   explanatory guidance remains reason-only instead of inventing incomplete
///   arguments. When a result is empty the nudge steers toward widening the scope
///   instead of drilling, because there is nothing to drill into.
///  </para>
/// </remarks>
public static class SteeringHints
{
    /// <summary>
    ///  The root pseudo-frame, whose presence as the dominant caller means the
    ///  focus frame is a top-level entry point.
    /// </summary>
    private const string RootFrame = "<root>";

    /// <summary>
    ///  The self-time pseudo-callee in a caller/callee view - the focus frame's own
    ///  execution, not a frame to drill into.
    /// </summary>
    private const string SelfFrame = "<self>";

    private const string UnknownFrame = "?";

    /// <summary>
    ///  The nudge emitted when a verb's scope contains no frames to drill into.
    /// </summary>
    private const string EmptyScope = "no frames in scope; widen the filter or check symbol resolution";

    private const int MaxNextStepFoldPatterns = 32;
    private const int MaxNextStepFoldPatternLength = 256;
    private const int MaxNextStepSymbolsLength = 1024;

    private static IReadOnlyList<string> Guidance(string reason) =>
        new SteeringHintSet([reason]);

    private static IReadOnlyList<string> Guidance(
        string reason,
        string operation,
        AnalysisNextStepArguments? arguments = null) =>
            new SteeringHintSet(
                [reason],
                [new AnalysisNextStep(reason) { Operation = operation, Arguments = arguments }]);

    private static AnalysisNextStep Step(
        string reason,
        string? operation = null,
        AnalysisNextStepArguments? arguments = null) =>
            new(reason) { Operation = operation, Arguments = arguments };

    private static AnalysisNextStepArguments CpuScopeArguments(
        ScopeRequest? scope,
        string root = "",
        string? frame = null,
        bool? callees = null) =>
            RankScopeArguments("cpu", scope, root, frame, callees);

    private static AnalysisNextStepArguments RankScopeArguments(
        string metric,
        ScopeRequest? scope,
        string root = "",
        string? frame = null,
        bool? callees = null)
    {
        IReadOnlyList<int>? processIds = null;
        int? processIdCount = null;
        bool processIdsTruncated = false;
        if (scope?.Selector is ProcessIdSelector ids)
        {
            processIdCount = ids.ProcessIds.Count;
            processIdsTruncated = ids.ProcessIds.Count > AnalysisScopeContext.MaxReportedProcessIds;
            processIds = processIdsTruncated
                ? [.. ids.ProcessIds.Take(AnalysisScopeContext.MaxReportedProcessIds)]
                : ids.ProcessIds;
        }

        TimeWindow? window = scope?.Window;
        return new AnalysisNextStepArguments
        {
            Metric = metric,
            Frame = frame,
            Root = string.IsNullOrEmpty(root) ? null : root,
            Process = (scope?.Selector as ProcessNameSelector)?.DisplayName,
            ProcessIds = processIds,
            ProcessIdCount = processIdCount,
            ProcessIdsTruncated = processIdsTruncated,
            IncludeChildren = scope is null ? null : scope.IncludeChildren,
            AllProcesses = scope?.IncludeAll == true ? true : null,
            Activity = scope?.ActivityName,
            FromMs = window is TimeWindow appliedWindow ? appliedWindow.StartMSec : null,
            ToMs = window is TimeWindow boundedWindow ? boundedWindow.EndMSec : null,
            Callees = callees
        };
    }

    /// <summary>
    ///  The next-step hints for a trace-info orientation: distinguish format support
    ///  from known capture enablement, and route symptoms only to analyses whose source
    ///  events were observed or whose recorder metadata establishes enablement.
    /// </summary>
    /// <param name="info">The trace info the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForTraceInfo(TraceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        bool hasAvailability = info.Analyses.Count > 0;
        HashSet<string> formatSupported = new(info.AvailableAnalyses, StringComparer.Ordinal);
        HashSet<string> analyses = !hasAvailability
            ? formatSupported
            : new HashSet<string>(
                info.Analyses
                    .Where(static pair => pair.Value is { FormatSupported: true, CaptureStatus: CaptureStatus.Enabled })
                    .Select(static pair => pair.Key),
                StringComparer.Ordinal);

        // Loader-produced objects route only to analyses known enabled. The fallback
        // keeps manually constructed legacy TraceInfo objects useful, but labels those
        // routes as format-supported because they carry no capture evidence.
        List<string> routes = [];
        if (analyses.Contains("cpu")) { routes.Add("CPU-bound -> cpu"); }

        List<string> blocked = [];
        if (analyses.Contains("contention")) { blocked.Add("contention"); }
        if (analyses.Contains("wait")) { blocked.Add("wait"); }
        if (analyses.Contains("threadpool")) { blocked.Add("threadpool"); }
        if (analyses.Contains("threadtime")) { blocked.Add("threadtime"); }
        if (blocked.Count > 0)
        {
            routes.Add($"slow but low CPU / does not scale -> {string.Join(", ", blocked)}");
        }

        List<string> memory = [];
        if (analyses.Contains("alloc")) { memory.Add("alloc"); }
        if (analyses.Contains("gcstats")) { memory.Add("gcstats"); }
        if (memory.Count > 0)
        {
            routes.Add($"high allocation rate or GC pauses -> {string.Join(", ", memory)}");
        }

        if (analyses.Contains("diskio"))
        {
            routes.Add("waiting on disk / heavy file I/O -> diskio");
        }

        if (analyses.Contains("exceptions"))
        {
            routes.Add("frequent exceptions -> exceptions");
        }

        if (analyses.Contains("activity"))
        {
            routes.Add("one request / endpoint / job is slow -> activity");
        }

        List<string> hints = [];

        if (routes.Count > 0)
        {
            string evidence = hasAvailability ? "known-enabled" : "format-supported";
            hints.Add($"{evidence} symptom routes - {string.Join("; ", routes)}");
        }

        if (hasAvailability)
        {
            string[] unknown =
            [
                .. info.Analyses
                    .Where(static pair => pair.Value is { FormatSupported: true, CaptureStatus: CaptureStatus.Unknown })
                    .Select(static pair => pair.Key)
            ];

            if (unknown.Length > 0)
            {
                hints.Add(
                    $"capture status unknown for: {string.Join(", ", unknown)}; "
                        + "absence of events is not proof the provider was disabled");
            }
        }

        if (info.SourceResolution is SourceResolutionInfo
            {
                PdbIdentityMismatchModules.Count: > 0
            } mismatchSource)
        {
            string mismatches = string.Join(", ", mismatchSource.PdbIdentityMismatchModules.Take(3));
            hints.Add($"PDB identity mismatch for: {mismatches}; the supplied build output does not match the trace-recorded GUID/age - use symbols from the captured run");
        }

        if (info.SourceResolution is SourceResolutionInfo
            {
                SampledManagedFrameCount: > 0,
                SourceResolutionRate: < SymbolGate.MinimumResolutionRate
            } source)
        {
            string affected = source.HighestUnmappedModules.Count == 0
                ? "sampled managed modules"
                : string.Join(", ", source.HighestUnmappedModules.Take(3));

            hints.Add($"method-name resolution ({FormatRate(info.SymbolResolutionRate)}) is separate from source mapping ({FormatRate(source.SourceResolutionRate)}); affected: {affected}; source lines require exact matching PDBs - retry with --symbols pointing at the recorded build output (for BenchmarkDotNet, the generated child output)");
            if (source.HighestUnmappedMethods.Count > 0)
            {
                hints.Add(
                    $"named managed frames without source: {source.UnmappedNamedManagedFrameCount}; "
                        + "inspect sourceResolution.highestUnmappedMethods");
            }
        }

        return new SteeringHintSet(hints);
    }

    private static string FormatRate(double value) =>
        $"{(value * 100.0).ToString("0", CultureInfo.InvariantCulture)}%";

    /// <summary>
    ///  The next-step hints for a self-time or inclusive-time ranking: check
    ///  frame-name quality when the hottest frame is unresolved; otherwise
    ///  drill into its callers.
    /// </summary>
    /// <param name="ranking">The ranking the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ranking"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForRanking(RankingResult ranking) =>
        ForRanking(ranking, MetricInfo.Cpu);

    /// <summary>
    ///  The next-step hints for a ranking, preserving its metric and scope.
    /// </summary>
    /// <param name="ranking">The ranking the hints steer from.</param>
    /// <param name="metric">The metric the ranking carries.</param>
    /// <param name="scope">The process, activity, and time scope used to build the ranking.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">
    ///  <paramref name="ranking"/> or <paramref name="metric"/> is <see langword="null"/>.
    /// </exception>
    public static IReadOnlyList<string> ForRanking(
        RankingResult ranking,
        MetricInfo metric,
        ScopeRequest? scope) =>
            ForRanking(ranking, metric, scope, path: null);

    /// <summary>
    ///  The next-step hints for a ranking, constrained to follow-ups that preserve
    ///  the ranking's metric and scope.
    /// </summary>
    /// <param name="ranking">The ranking the hints steer from.</param>
    /// <param name="metric">The metric the ranking carries.</param>
    /// <param name="scope">Optional process, activity, and time scope used to build the ranking.</param>
    /// <param name="path">The trace path for a complete, scope-safe quality follow-up.</param>
    /// <param name="symbols">The local symbol directory used for this ranking, if any.</param>
    /// <param name="nativeSymbols">Whether native runtime symbol resolution was enabled for this ranking.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">
    ///  <paramref name="ranking"/> or <paramref name="metric"/> is <see langword="null"/>.
    /// </exception>
    public static IReadOnlyList<string> ForRanking(
        RankingResult ranking,
        MetricInfo metric,
        ScopeRequest? scope = null,
        string? path = null,
        string? symbols = null,
        bool nativeSymbols = false)
    {
        ArgumentNullException.ThrowIfNull(ranking);
        ArgumentNullException.ThrowIfNull(metric);

        if (ranking.Rows.Count == 0)
        {
            return Guidance(EmptyScope);
        }

        if (!string.Equals(metric.Name, MetricInfo.Cpu.Name, StringComparison.Ordinal))
        {
            string reason =
                $"refine the {metric.Name} ranking with self/inclusive measure, root, or time; callers, lines, heatmap, and tree analyze CPU only";

            return Guidance(reason);
        }

        if (scope?.ActivityName is not null || scope?.Window is not null)
        {
            string reason =
                "this CPU ranking is activity/time-scoped; callers, lines, heatmap, and tree cannot preserve that slice - refine it with self/inclusive measure or root in rank";

            if (IsUnresolvedFrame(ranking.Rows[0].Frame))
            {
                string quality = UnresolvedRankingHint(
                    ranking.Rows[0], scope, ranking.RootFrame, symbols, nativeSymbols);

                return new SteeringHintSet(
                    [quality, reason],
                    [Step(quality), Step(reason, "rank", CpuScopeArguments(scope, ranking.RootFrame))]);
            }

            return Guidance(reason, "rank", CpuScopeArguments(scope, ranking.RootFrame));
        }

        if (IsUnresolvedFrame(ranking.Rows[0].Frame))
        {
            string quality = UnresolvedRankingHint(
                ranking.Rows[0], scope, ranking.RootFrame, symbols, nativeSymbols);

            RankRow? resolved = ranking.Rows.FirstOrDefault(static row => IsResolvedFrame(row.Frame));
            if (resolved is null)
            {
                return new SteeringHintSet(
                    [quality],
                    [FrameNameQualityStep(quality, ranking.RootFrame, scope, path, symbols, nativeSymbols)]);
            }

            string optional = PreserveLocalSymbols(
                PreserveCpuScope(
                    $"optional: inspect the first resolved row separately with: callers {QuotePowerShellArgument(resolved.Frame)}",
                    ranking.RootFrame,
                    scope),
                symbols);

            if (nativeSymbols)
            {
                optional += "; callers cannot reproduce native-symbol resolution";
            }

            return new SteeringHintSet(
                [quality, optional],
                [
                    FrameNameQualityStep(quality, ranking.RootFrame, scope, path, symbols, nativeSymbols),
                    CpuCallerStep(
                        optional, ranking.RootFrame, scope, resolved.Frame, symbols, nativeSymbols,
                        requireCompleteProcessIds: true)
                ]);
        }

        string hint = $"drill into the hot frame with: callers {ranking.Rows[0].Frame}";
        string message = PreserveLocalSymbols(
            PreserveCpuScope(hint, ranking.RootFrame, scope), symbols);

        if (nativeSymbols)
        {
            message += "; callers cannot reproduce native-symbol resolution";
        }

        return new SteeringHintSet(
            [message],
            [CpuCallerStep(
                message, ranking.RootFrame, scope, ranking.Rows[0].Frame, symbols, nativeSymbols)]);
    }

    /// <summary>
    ///  The next-step hints for a callers report: check quality when the focus or
    ///  dominant caller is unresolved, otherwise continue up the stack or identify
    ///  a top-level entry point.
    /// </summary>
    /// <param name="callers">The callers report the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callers"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForCallers(CallersResult callers) =>
        ForCallers(callers, "");

    /// <summary>
    ///  The next-step hints for a callers report, preserving its root and process
    ///  scope on every actionable follow-up.
    /// </summary>
    /// <param name="callers">The callers report the hints steer from.</param>
    /// <param name="root">The root frame the callers analysis was scoped to, or empty for none.</param>
    /// <param name="scope">Optional process scope used to build the callers report.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callers"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForCallers(
        CallersResult callers,
        string root,
        ScopeRequest? scope = null)
    {
        ArgumentNullException.ThrowIfNull(callers);

        if (callers.Callers.Count == 0)
        {
            return Guidance(EmptyScope);
        }

        List<string> hints = [];
        List<AnalysisNextStep> steps = [];

        string topCaller = callers.Callers[0].Caller;
        if (IsUnresolvedFrame(callers.Focus) || IsUnresolvedFrame(topCaller))
        {
            string quality = IsUnresolvedFrame(callers.Focus)
                ? $"the '{callers.Focus}' focus can group unrelated unresolved frames; "
                    + $"its caller weights remain relative to the full target; {FrameNameQualityHint(scope)}"
                : $"the leading caller '{topCaller}' is unresolved "
                    + $"({callers.Callers[0].PercentOfTarget.ToString("0.##", CultureInfo.InvariantCulture)}% of target); "
                    + $"its weight remains in the caller denominator; {FrameNameQualityHint(scope)}";

            hints.Add(quality);
            steps.Add(Step(quality));

            CallerRow? resolved = callers.Callers.FirstOrDefault(static row => IsResolvedFrame(row.Caller));
            if (resolved is not null)
            {
                string optional = PreserveCpuScope(
                    $"optional: inspect the first resolved caller separately with: callers {QuotePowerShellArgument(resolved.Caller)}",
                    root,
                    scope);

                hints.Add(optional);
                steps.Add(CpuCallerStep(
                    optional, root, scope, resolved.Caller,
                    symbols: null, nativeSymbols: false, requireCompleteProcessIds: true));
            }
        }
        else if (string.Equals(topCaller, RootFrame, StringComparison.Ordinal))
        {
            string reason = "the focus frame is called directly from the root; it is a top-level entry point";
            hints.Add(reason);
            steps.Add(Step(reason));
        }
        else
        {
            string reason = PreserveCpuScope($"continue up the stack with: callers {topCaller}", root, scope);
            hints.Add(reason);
            steps.Add(Step(reason, "callers", CpuScopeArguments(scope, root, topCaller)));
        }

        // With a caller/callee view, also point down into the heaviest real callee, skipping
        // the <self> self-time pseudo-callee since it is not a frame to drill into.
        if (callers.Callees is { } callees)
        {
            foreach (CalleeRow callee in callees)
            {
                if (!string.Equals(callee.Callee, SelfFrame, StringComparison.Ordinal)
                    && !IsUnresolvedFrame(callee.Callee))
                {
                    string reason = PreserveCpuScope(
                        $"continue down into the callee with: callers {callee.Callee} --callees",
                        root,
                        scope);

                    hints.Add(reason);
                    steps.Add(Step(
                        reason,
                        "callers",
                        CpuScopeArguments(scope, root, callee.Callee, callees: true)));

                    break;
                }
            }
        }

        return new SteeringHintSet(hints, steps);
    }

    private static string PreserveCpuScope(string hint, string root, ScopeRequest? scope)
    {
        if (!string.IsNullOrEmpty(root))
        {
            hint = $"{hint} --root {QuotePowerShellArgument(root)}";
        }

        hint = scope?.Selector switch
        {
            ProcessNameSelector name => $"{hint} --process {QuotePowerShellArgument(name.DisplayName)}",
            // Comma-separated, not repeated: a repeated --pid keeps only the last value.
            ProcessIdSelector ids => $"{hint} --pid {string.Join(",", ids.ProcessIds)}",
            _ => scope?.IncludeAll == true ? $"{hint} --all-processes" : hint
        };

        // Keyed on the descendant mode alone, not on the selector: the automatic scope
        // carries no selector but still picks a tree, so a hint that dropped its
        // --children exclude would silently widen a parent-only drill-down.
        return scope is { IncludeChildren: false } ? $"{hint} --children exclude" : hint;
    }

    private static string PreserveLocalSymbols(string hint, string? symbols)
    {
        if (string.IsNullOrEmpty(symbols))
        {
            return hint;
        }

        return $"{hint} --symbols {QuotePowerShellArgument(symbols)}";
    }

    private static bool IsUnresolvedFrame(string frame) =>
        string.Equals(frame, UnknownFrame, StringComparison.Ordinal)
            || frame.EndsWith("!?", StringComparison.Ordinal);

    private static bool IsResolvedFrame(string frame) =>
        !IsUnresolvedFrame(frame)
            && !string.Equals(frame, RootFrame, StringComparison.Ordinal)
            && !string.Equals(frame, SelfFrame, StringComparison.Ordinal);

    private static string FrameNameQualityHint(ScopeRequest? scope, string? symbols = null)
    {
        string command = PreserveLocalSymbols(PreserveCpuScope("info <trace>", "", scope), symbols);
        return $"check frame-name quality with: {command}";
    }

    private static AnalysisNextStep CpuCallerStep(
        string reason,
        string root,
        ScopeRequest? scope,
        string frame,
        string? symbols,
        bool nativeSymbols,
        bool requireCompleteProcessIds = false)
    {
        if (nativeSymbols
            || (requireCompleteProcessIds
                && scope?.Selector is ProcessIdSelector ids
                && ids.ProcessIds.Count > AnalysisScopeContext.MaxReportedProcessIds))
        {
            return Step(reason);
        }

        return Step(
            reason, "callers",
            CpuScopeArguments(scope, root, frame) with { Symbols = symbols });
    }

    private static AnalysisNextStep FrameNameQualityStep(
        string reason,
        string root,
        ScopeRequest? scope,
        string? path,
        string? symbols,
        bool nativeSymbols)
    {
        if (!string.IsNullOrEmpty(root)
            || scope?.ActivityName is not null
            || scope?.Window is not null
            || nativeSymbols
            || string.IsNullOrWhiteSpace(path)
            || scope?.Selector is ProcessIdSelector ids
                && ids.ProcessIds.Count > AnalysisScopeContext.MaxReportedProcessIds)
        {
            return Step(reason);
        }

        AnalysisNextStepArguments arguments = RankScopeArguments("cpu", scope) with
        {
            Path = path,
            Symbols = symbols,
            Metric = null
        };

        return Step(reason, "info", arguments);
    }

    private static string UnresolvedRankingHint(
        RankRow row,
        ScopeRequest? scope,
        string root,
        string? symbols,
        bool nativeSymbols)
    {
        string hint = $"the leading '{row.Frame}' row groups unresolved frames "
            + $"({row.PercentOfScope.ToString("0.##", CultureInfo.InvariantCulture)}% of scoped weight); "
            + $"keep that weight in the denominator and {FrameNameQualityHint(scope, symbols)}";

        if (!string.IsNullOrEmpty(root))
        {
            hint += "; info does not preserve the root subtree";
        }

        if (scope?.ActivityName is not null || scope?.Window is not null)
        {
            hint += "; info does not preserve the activity/time slice";
        }

        if (nativeSymbols)
        {
            hint += "; info does not reproduce native-symbol resolution";
        }

        return hint;
    }

    // Hints use PowerShell command syntax throughout the shipped Windows-first docs.
    // Single-quoted arguments preserve whitespace, double quotes, dollar signs, and
    // backticks; PowerShell represents an embedded apostrophe by doubling it.
    private static string QuotePowerShellArgument(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    ///  The next-step hints for a ranking diff: check frame-name quality when
    ///  the largest changed row is unresolved, otherwise drill into that frame.
    /// </summary>
    /// <param name="diff">The ranking diff the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForDiff(RankingDiffResult diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (diff.Kind == RankingDiffResult.ManifestKind)
        {
            (RankingDiffCaseResult Case, DiffRow Row)? largest = diff.Cases
                .SelectMany(static captureCase => captureCase.Rows.Select(row => (captureCase, row)))
                .OrderByDescending(static pair => Math.Round(Math.Abs(pair.row.PercentagePointChange), 9))
                .ThenBy(static pair => pair.row.Frame, StringComparer.Ordinal)
                .Select(static pair => ((RankingDiffCaseResult Case, DiffRow Row)?)pair)
                .FirstOrDefault();

            if (largest is null)
            {
                return Guidance("paired manifest cases have no changed ranking rows");
            }

            string identity = string.IsNullOrEmpty(largest.Value.Case.Parameters)
                ? largest.Value.Case.Benchmark
                : $"{largest.Value.Case.Benchmark} ({largest.Value.Case.Parameters})";

            if (IsUnresolvedFrame(largest.Value.Row.Frame))
            {
                string quality =
                    $"largest normalized change in {identity} is unresolved ('{largest.Value.Row.Frame}'); "
                        + "check frame-name quality in both case traces before attributing it";

                DiffRow? resolved = largest.Value.Case.Rows.FirstOrDefault(static row => IsResolvedFrame(row.Frame));
                return resolved is null
                    ? Guidance(quality)
                    : new SteeringHintSet(
                        [quality, $"optional: inspect the first resolved change, {resolved.Frame}, in the paired case traces without dropping their scopes"]);
            }

            return Guidance(
                $"largest normalized change is {largest.Value.Row.Frame} in {identity}; drill into the paired traces with callers");
        }

        if (diff.Rows.Count == 0)
        {
            return Guidance("the two rankings match in scope; no frames changed");
        }

        if (diff.FrameNamesBounded)
        {
            return Guidance("one or more changed frame names were shortened for output; narrow the diff with a root before drilling");
        }

        string top = diff.Rows[0].Frame;
        if (IsUnresolvedFrame(top))
        {
            string quality =
                $"the largest changed row '{top}' is unresolved; its before/after weights remain in the diff; "
                    + "check frame-name quality in both traces before attributing the change";

            DiffRow? resolved = diff.Rows.FirstOrDefault(static row => IsResolvedFrame(row.Frame));
            if (resolved is null)
            {
                return Guidance(quality);
            }

            string optional = $"optional: pick one trace and preserve its original scope before inspecting the first "
                + $"resolved changed row with callers {QuotePowerShellArgument(resolved.Frame)}; it does not explain '{top}'";

            return new SteeringHintSet([quality, optional]);
        }

        string reason = $"the largest change is {top}; drill into it with: callers {top}";
        return Guidance(
            reason,
            "callers",
            new AnalysisNextStepArguments { Metric = "cpu", Frame = top });
    }

    /// <summary>
    ///  Next step after a manifest batch summary.
    /// </summary>
    /// <param name="batch">The compact results for all analyzed manifest cases.</param>
    /// <returns>Guidance for drilling into the hottest addressable case.</returns>
    public static IReadOnlyList<string> ForBatch(BatchRankingResult batch) =>
        ForBatch(batch, scope: null, symbols: null, foldPatterns: null);

    /// <summary>
    ///  Next step after a manifest batch summary, preserving query overrides.
    /// </summary>
    /// <param name="batch">The compact batch result.</param>
    /// <param name="scope">Explicit process scope used by the batch, or <see langword="null"/>.</param>
    /// <param name="symbols">Explicit symbol-directory override, or <see langword="null"/>.</param>
    /// <param name="foldPatterns">Resolved fold patterns used by the batch.</param>
    /// <returns>The steering hints, including an actionable rank step when a case can be addressed.</returns>
    public static IReadOnlyList<string> ForBatch(
        BatchRankingResult batch,
        ScopeRequest? scope,
        string? symbols,
        IReadOnlyList<string>? foldPatterns)
    {
        ArgumentNullException.ThrowIfNull(batch);
        BatchRankingCaseResult? hottest = batch.Cases
            .Where(static captureCase => captureCase.TopFrame is not null)
            .OrderByDescending(static captureCase => captureCase.TopPercentOfScope)
            .FirstOrDefault();

        if (hottest is null)
        {
            return Guidance("no manifest case produced a ranked frame; inspect case warnings and capture availability");
        }

        if (string.IsNullOrEmpty(hottest.CaseId))
        {
            return Guidance($"inspect {hottest.Benchmark} in detail against: {hottest.TracePath}");
        }

        string reason = $"inspect manifest case '{hottest.CaseId}' in detail";
        if (!CanPreserveBatchNextStepOverrides(symbols, foldPatterns))
        {
            return Guidance(reason);
        }

        AnalysisNextStepArguments arguments = RankScopeArguments(batch.Metric, scope, batch.RootFrame) with
        {
            ManifestPath = batch.ManifestPath,
            CaseId = hottest.CaseId,
            Measure = batch.Measure,
            Symbols = symbols,
            IncludeChildren = scope is { IncludeChildren: false } ? false : null,
            Fold = foldPatterns is not null
                && !foldPatterns.SequenceEqual(FrameNames.DefaultFoldPatterns)
                    ? [.. foldPatterns]
                    : null
        };

        return Guidance(reason, "rank", arguments);
    }

    private static bool CanPreserveBatchNextStepOverrides(
        string? symbols,
        IReadOnlyList<string>? foldPatterns)
    {
        if (symbols is { } symbolPath
            && (symbolPath.Length > MaxNextStepSymbolsLength || symbolPath.Any(char.IsControl)))
        {
            return false;
        }

        if (foldPatterns is null)
        {
            return true;
        }

        if (foldPatterns.Count > MaxNextStepFoldPatterns)
        {
            return false;
        }

        foreach (string pattern in foldPatterns)
        {
            if (pattern is null
                || pattern.Length > MaxNextStepFoldPatternLength
                || pattern.Any(char.IsControl))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///  The next-step hints for a timeline: name the busiest window and the scoped
    ///  ranking that drills it, turning the orientation view into the next command.
    /// </summary>
    /// <param name="timeline">The timeline the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="timeline"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForTimeline(TimelineResult timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        if (timeline.Snapshot is TimelineSnapshot snapshot)
        {
            string window = $"{FormatMs(timeline.FromMs)}-{FormatMs(timeline.ToMs)} ms";
            if (snapshot.Cpu.SampleCount > 0)
            {
                return SnapshotDrillGuidance("CPU work", "cpu", timeline, window);
            }

            if (snapshot.Alloc.Types.Count > 0)
            {
                return SnapshotDrillGuidance("allocation sites", "alloc", timeline, window);
            }

            if (snapshot.Exceptions.Types.Count > 0)
            {
                return SnapshotDrillGuidance("exception paths", "exceptions", timeline, window);
            }

            return Guidance($"snapshot covers {window}; widen --window if the bounded evidence is too thin");
        }

        // Prefer the CPU lane - the canonical "find the window, then rank it" loop - then
        // the other rankable lanes, and finally GC activity. A null or all-zero lane has
        // nothing to point at, so it is skipped.
        if (TryPeakBucket(timeline.Cpu, static bucket => bucket.SampleCount, out int cpuIndex))
        {
            return DrillWindowGuidance("CPU", "cpu", timeline, cpuIndex);
        }

        if (TryPeakBucket(timeline.Alloc, static bucket => bucket.Count, out int allocIndex))
        {
            return DrillWindowGuidance("allocation", "alloc", timeline, allocIndex);
        }

        if (TryPeakBucket(timeline.Exceptions, static bucket => bucket.Count, out int exceptionIndex))
        {
            return DrillWindowGuidance("exception", "exceptions", timeline, exceptionIndex);
        }

        if (TryPeakBucket(timeline.Gc, static bucket => bucket.Count, out int gcIndex))
        {
            (double start, double end) = WindowOf(timeline, gcIndex);
            string reason = $"busiest GC window is bucket {gcIndex} ({FormatMs(start)}-{FormatMs(end)} ms); inspect collections with: gcstats";
            return Guidance(reason, "gc");
        }

        return Guidance("the timeline is empty in every requested lane; widen the window or check the capture carries these events");
    }

    /// <summary>
    ///  The next-step hints for a lifecycle report: name the phase that dominates the
    ///  command's wall clock and the drill-down that explains it.
    /// </summary>
    /// <param name="lifecycle">The lifecycle report the hints steer from.</param>
    /// <returns>The steering hints, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lifecycle"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> ForLifecycle(LifecycleResult lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        if (lifecycle.InvocationCount == 0)
        {
            return Guidance(
                "no invocation matched; list what the capture holds with: processes",
                "processes");
        }

        LifecyclePhase? rootLifetime = lifecycle.Phases.FirstOrDefault(static phase => phase.Phase == "root lifetime");
        if (rootLifetime is null)
        {
            return Guidance("every invocation was clipped to the capture window; recapture with the command launched inside the session");
        }

        // The phase that owns the most median wall clock, excluding the root lifetime it
        // is measured against. Naming it is the whole point of the report: a command that
        // spends its time before the first child is a loader problem, and one that spends
        // it inside the child is a child-code problem, and those drill differently.
        LifecyclePhase? dominant = lifecycle.Phases
            .Where(static phase => phase.Phase != "root lifetime")
            .OrderByDescending(static phase => phase.MedianMs)
            .FirstOrDefault();

        List<string> hints = [];
        if (dominant is not null)
        {
            hints.Add(
                $"the median {dominant.Phase} is {FormatMs(dominant.MedianMs)} ms of a "
                    + $"{FormatMs(rootLifetime.MedianMs)} ms median root lifetime");
        }

        // Wall clock and sampled CPU answer different questions, and the gap between them
        // is the blocked time this report exists to expose.
        double sampledCpuMs = lifecycle.TotalRootCpuMs + lifecycle.TotalChildCpuMs;
        hints.Add(
            $"wall clock is not CPU: rank sampled work in the same processes with: cpu, "
                + $"and time it against {FormatMs(sampledCpuMs)} ms of sampled CPU across the matched tree");

        return new SteeringHintSet(hints);
    }

    // The index of the highest-weight bucket in a lane, or false when the lane is absent
    // or every bucket is empty.
    private static bool TryPeakBucket<T>(IReadOnlyList<T>? lane, Func<T, long> weight, out int index)
    {
        index = -1;
        if (lane is null)
        {
            return false;
        }

        long best = 0;
        for (int i = 0; i < lane.Count; i++)
        {
            long value = weight(lane[i]);
            if (value > best)
            {
                best = value;
                index = i;
            }
        }

        return index >= 0;
    }

    // The [start, end] millisecond bounds of a bucket. Kept as doubles so a sub-millisecond
    // bucket (a short capture divided into many buckets) is not rounded to a degenerate or
    // shifted window in the drill command.
    private static (double Start, double End) WindowOf(TimelineResult timeline, int index)
    {
        double start = timeline.FromMs + (index * timeline.BucketSizeMs);
        double end = timeline.FromMs + ((index + 1) * timeline.BucketSizeMs);
        return (start, end);
    }

    // Formats a millisecond bound for a hint: invariant culture (the form the --time parser
    // reads back) with trailing zeros trimmed, so a whole-millisecond bound stays "60" while a
    // sub-millisecond bound keeps its precision.
    private static string FormatMs(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // The drill hint for a rankable lane: name the busy window and the scoped ranking
    // that continues the investigation into it, carrying the timeline's process scope so
    // the follow-up ranking stays on the same process tree the timeline was read from.
    private static string DrillWindowHint(string laneLabel, string metric, TimelineResult timeline, int index)
    {
        (double start, double end) = WindowOf(timeline, index);
        string from = FormatMs(start);
        string to = FormatMs(end);
        return $"busiest {laneLabel} window is bucket {index} ({from}-{to} ms); "
            + $"scope a ranking with: rank --metric {metric} --time {from},{to}{ProcessScope(timeline)}";
    }

    private static IReadOnlyList<string> DrillWindowGuidance(
        string laneLabel,
        string metric,
        TimelineResult timeline,
        int index)
    {
        (double start, double end) = WindowOf(timeline, index);
        if (!CanRepresentTimelineScope(timeline, out int processIdCount))
        {
            return UnrepresentableTimelineScopeGuidance(timeline, start, end, processIdCount);
        }

        string reason = DrillWindowHint(laneLabel, metric, timeline, index);
        return Guidance(
            reason,
            "rank",
            TimelineScopeArguments(metric, timeline) with
            {
                FromMs = start,
                ToMs = end
            });
    }

    private static IReadOnlyList<string> SnapshotDrillGuidance(
        string subject,
        string metric,
        TimelineResult timeline,
        string window)
    {
        if (!CanRepresentTimelineScope(timeline, out int processIdCount))
        {
            return UnrepresentableTimelineScopeGuidance(
                timeline,
                timeline.FromMs,
                timeline.ToMs,
                processIdCount);
        }

        string reason = $"snapshot covers {window}; drill the top {subject} with: "
            + $"rank --metric {metric} --time {FormatMs(timeline.FromMs)},{FormatMs(timeline.ToMs)}{ProcessScope(timeline)}";

        return Guidance(
            reason,
            "rank",
            TimelineScopeArguments(metric, timeline));
    }

    private static AnalysisNextStepArguments TimelineScopeArguments(string metric, TimelineResult timeline)
    {
        AppliedProcessScope? scope = timeline.AppliedProcessScope;
        IReadOnlyList<int>? sourceIds = scope?.Mode switch
        {
            "ids" => scope.RequestedProcessIds,
            "automatic" => scope.RootProcessIds,
            _ => null
        };

        int? processIdCount = sourceIds?.Count;
        bool processIdsTruncated = sourceIds is { Count: > AnalysisScopeContext.MaxReportedProcessIds };
        IReadOnlyList<int>? processIds = processIdsTruncated
            ? [.. sourceIds!.Take(AnalysisScopeContext.MaxReportedProcessIds)]
            : sourceIds;

        return new AnalysisNextStepArguments
        {
            Metric = metric,
            Process = scope is { Mode: "name" } ? scope.Process : null,
            ProcessIds = processIds,
            ProcessIdCount = processIdCount,
            ProcessIdsTruncated = processIdsTruncated,
            IncludeChildren = scope is null or { Mode: "all" } ? null : scope.IncludeChildren,
            AllProcesses = scope is { Mode: "all" } ? true : null,
            FromMs = timeline.FromMs,
            ToMs = timeline.ToMs
        };
    }

    // The safely quoted " --process <name>" suffix a scoped timeline's drill hint
    // carries so the follow-up ranking stays on the same process tree, or empty when
    // the timeline spanned every process.
    private static string ProcessScope(TimelineResult timeline)
    {
        AppliedProcessScope? scope = timeline.AppliedProcessScope;
        if (scope is null)
        {
            return string.Empty;
        }

        string suffix = scope.Mode switch
        {
            "all" => " --all-processes",
            "name" when scope.Process is { Length: > 0 } process => $" --process {QuotePowerShellArgument(process)}",
            "ids" => ProcessIdScope(scope.RequestedProcessIds),
            "automatic" => ProcessIdScope(scope.RootProcessIds),
            _ => string.Empty
        };

        return suffix.Length > 0 && scope.Mode != "all" && !scope.IncludeChildren
            ? $"{suffix} --children exclude"
            : suffix;
    }

    private static string ProcessIdScope(IReadOnlyList<int> processIds)
    {
        if (processIds.Count == 0)
        {
            return string.Empty;
        }

        return $" --pid {string.Join(",", processIds)}";
    }

    private static bool CanRepresentTimelineScope(TimelineResult timeline, out int processIdCount)
    {
        AppliedProcessScope? scope = timeline.AppliedProcessScope;
        IReadOnlyList<int>? processIds = scope?.Mode switch
        {
            "ids" => scope.RequestedProcessIds,
            "automatic" => scope.RootProcessIds,
            _ => null
        };

        processIdCount = processIds?.Count ?? 0;
        return processIdCount <= AnalysisScopeContext.MaxReportedProcessIds
            && scope is not { Mode: "automatic", RootProcessIdsReplayable: false };
    }

    private static IReadOnlyList<string> UnrepresentableTimelineScopeGuidance(
        TimelineResult timeline,
        double startMs,
        double endMs,
        int processIdCount)
    {
        // Reuse makes every exact-id replay unrunnable regardless of how many roots
        // fit in the bounded argument list, so report it before the count limit.
        if (timeline.AppliedProcessScope is { Mode: "automatic", RootProcessIdsReplayable: false })
        {
            return Guidance(
                "the automatic process scope includes a root pid reused by multiple process instances, so no exact "
                    + $"--pid follow-up can be generated; inspect processes, choose an explicit selector, and rerun rank "
                    + $"with --time {FormatMs(startMs)},{FormatMs(endMs)}");
        }

        string selectorGuidance = timeline.AppliedProcessScope?.Mode == "automatic"
            ? "choose a narrower --process or --pid selector"
            : "reuse the original process selector";

        return Guidance(
            $"the exact process scope has {processIdCount} ids, exceeding the bounded follow-up limit; "
                + $"{selectorGuidance} and rerun rank with --time {FormatMs(startMs)},{FormatMs(endMs)}");
    }
}
