// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Filtrace.Output;

namespace Filtrace.Tracing;

/// <summary>
///  Aggregates weighted CPU samples into self-time, inclusive-time and
///  caller rankings, folding JIT-helper sampling artifacts and the synthetic
///  BenchmarkDotNet markers back into the real methods that incurred them.
/// </summary>
/// <remarks>
///  <para>
///   This is a direct port of the aggregation in
///   <c>tools/Get-TraceHotspots.ps1</c>. Two facts make a naive "top self-time"
///   reading wrong, and this type corrects both:
///  </para>
///  <para>
///   The leaf self-time of every sample collapses into a synthetic
///   <c>CPU_TIME</c> / <c>UNMANAGED_CODE_TIME</c> marker, so an unfolded
///   self-time aggregation reports almost all time against that marker. And
///   when a sample's instruction pointer lands inside a JIT helper (a write
///   barrier, a memmove, the GC-poll thunk at a loop back-edge), the
///   managed-only walker resolves the leaf to the helper thunk instead of the
///   method whose hot loop is actually running.
///  </para>
///  <para>
///   Self-time walks up past folded frames to credit the nearest real leaf;
///   inclusive-time simply skips folded frames. The result matches what PerfView
///   produces with <c>/FoldPats</c>.
///  </para>
/// </remarks>
public sealed partial class FoldingAggregator
{
    /// <summary>
    ///  The largest call-tree depth <see cref="CallTree"/> accepts. The tree is
    ///  materialized and rendered by recursion whose depth equals the tree height,
    ///  which the depth bound caps; limiting that bound keeps a deep (possibly
    ///  hand-authored) input trace from driving either recursion into a
    ///  <see cref="StackOverflowException"/>. It is far beyond any readable tree depth.
    /// </summary>
    public const int MaxTreeDepth = 1024;

    // The JSON scaffolding around one ranked row - the property names, the punctuation,
    // and the numeric weight and percentage - which the per-row estimate adds to the
    // frame name.
    private const int RankRowScaffoldTokens = 16;

    // The same scaffolding for the other row shapes this type produces.
    private const int CallerRowScaffoldTokens = 16;
    private const int LineRowScaffoldTokens = 19;
    private const int HeatLineScaffoldTokens = 27;

    private readonly StackSampleSource _source;
    private readonly IReadOnlyList<SampleStack> _samples;

    // A single FoldingAggregator is cached on LoadedTrace and can be queried
    // concurrently through the singleton TraceStore, so the short-name cache must
    // be safe for parallel readers and writers.
    private readonly ConcurrentDictionary<string, string> _shortCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RootScopeCoverage> _rootCoverageCache = new(StringComparer.Ordinal);

    /// <summary>
    ///  Initializes a new <see cref="FoldingAggregator"/> over the given source.
    /// </summary>
    /// <param name="source">The stack-sample source to rank.</param>
    public FoldingAggregator(StackSampleSource source)
    {
        _source = source;
        _samples = source.Samples;
    }

    /// <summary>
    ///  The metric the ranked sample weights are measured in.
    /// </summary>
    public MetricInfo Metric => _source.Metric;

    /// <summary>
    ///  Measures a root frame's stack-ancestry coverage against this aggregator's
    ///  complete, already externally scoped source.
    /// </summary>
    /// <param name="rootFrame">Substring identifying the root frame.</param>
    /// <returns>The pre-root and retained metric weight and record counts.</returns>
    /// <exception cref="ArgumentException"><paramref name="rootFrame"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rootFrame"/> is <see langword="null"/>.</exception>
    public RootScopeCoverage GetRootScopeCoverage(string rootFrame)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootFrame);
        return _rootCoverageCache.GetOrAdd(rootFrame, CalculateRootScopeCoverage);
    }

    private RootScopeCoverage CalculateRootScopeCoverage(string rootFrame)
    {
        double availableWeight = 0.0;
        double retainedWeight = 0.0;
        int retainedRecords = 0;

        foreach (SampleStack sample in _samples)
        {
            availableWeight += sample.Weight;
            if (FrameNames.TryFindRootStart(sample.Frames, rootFrame, out _)
                && sample.Frames.Count > 0)
            {
                retainedWeight += sample.Weight;
                retainedRecords++;
            }
        }

        double retainedPercent = availableWeight > 0.0
            ? retainedWeight / availableWeight * 100.0
            : 0.0;

        bool recordsAvailable = _source.RecordSemantics != StackRecordSemantics.Unavailable;
        return new RootScopeCoverage(
            availableWeight,
            retainedWeight,
            retainedPercent,
            recordsAvailable ? _samples.Count : null,
            recordsAvailable ? retainedRecords : null);
    }

    /// <summary>
    ///  Lists every process that owns samples, ranked by summed sample weight, so a
    ///  multi-process capture can be scoped to the right one before ranking.
    /// </summary>
    /// <returns>The process inventory: each process's sample count, weight, and share.</returns>
    /// <remarks>
    ///  <para>
    ///   No folding applies - a process owns a sample regardless of which leaf the
    ///   sample resolved to - so this counts raw samples and sums their weights by the
    ///   process label the reader tagged each sample with. Ties in weight break by the
    ///   process label so the order is deterministic.
    ///  </para>
    /// </remarks>
    public ProcessListResult Processes()
    {
        Dictionary<string, (int Count, double Weight)> byProcess = new(StringComparer.Ordinal);
        double totalWeight = 0.0;
        foreach (SampleStack sample in _samples)
        {
            (int Count, double Weight) accumulated = byProcess.GetValueOrDefault(sample.Process);
            byProcess[sample.Process] = (accumulated.Count + 1, accumulated.Weight + sample.Weight);
            totalWeight += sample.Weight;
        }

        List<ProcessSummary> processes = new(byProcess.Count);
        foreach (KeyValuePair<string, (int Count, double Weight)> entry in byProcess)
        {
            double percent = totalWeight > 0.0 ? entry.Value.Weight / totalWeight * 100.0 : 0.0;
            processes.Add(new ProcessSummary(entry.Key, entry.Value.Count, entry.Value.Weight, percent));
        }

        // Highest weight first; ties break by process label so the ranking is stable
        // and the JSON output deterministic.
        processes.Sort(static (a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Process, b.Process);
        });

        return new ProcessListResult(totalWeight, _samples.Count, processes);
    }

    private string ShortOf(string name) => _shortCache.GetOrAdd(name, static n => FrameNames.Short(n));

    /// <summary>
    ///  Computes the folded self-time ranking.
    /// </summary>
    /// <param name="rootFrame">Substring scoping the ranking to a subtree, or empty for the whole trace.</param>
    /// <param name="foldPatterns">Leaf-frame fold patterns.</param>
    /// <param name="top">Maximum number of rows to return.</param>
    /// <returns>The self-time ranking.</returns>
    public RankingResult SelfTime(string rootFrame, IReadOnlyList<string> foldPatterns, int top)
    {
        Regex[] fold = FrameNames.CompileFoldPatterns(foldPatterns);
        Dictionary<string, double> selfTime = new(StringComparer.Ordinal);
        double total = 0.0;
        int contributingRecordCount = 0;

        foreach (SampleStack sample in _samples)
        {
            IReadOnlyList<string> frames = sample.Frames;
            bool include = FrameNames.TryFindRootStart(frames, rootFrame, out int startIdx);
            if (!include || frames.Count == 0)
            {
                continue;
            }

            total += sample.Weight;
            contributingRecordCount++;

            int leafIdx = frames.Count - 1;
            while (leafIdx > startIdx && FrameNames.IsFolded(ShortOf(frames[leafIdx]), fold))
            {
                leafIdx--;
            }

            string leaf = ShortOf(frames[leafIdx]);
            selfTime.TryGetValue(leaf, out double current);
            selfTime[leaf] = current + sample.Weight;
        }

        return new RankingResult(
            total,
            rootFrame,
            RankRows(selfTime, total, top),
            AvailableRecordCount(contributingRecordCount));
    }

    /// <summary>
    ///  Classifies self-time by runtime work category - zeroing, copying, GC,
    ///  write-barrier, JIT, or other - so a CPU profile can be summarized as "where did
    ///  the time go: zeroing memory? copying strings? in the GC?".
    /// </summary>
    /// <param name="rootFrame">Substring scoping the classification to a subtree, or empty for the whole trace.</param>
    /// <returns>The categories, ranked by self-time weight.</returns>
    /// <remarks>
    ///  <para>
    ///   The self-time leaf of each sample is found the same way <see cref="SelfTime"/>
    ///   finds it, but folding is fixed to the synthetic sample markers only
    ///   (<see cref="FrameNames.MarkerOnlyFoldPatterns"/>): the JIT-helper thunks the
    ///   categories classify (memset / memcpy / write barriers) must not be folded away,
    ///   or the work being classified would be folded into its managed caller and lost.
    ///   The leaf is then bucketed by <see cref="FrameCategories.Classify"/>.
    ///  </para>
    /// </remarks>
    public ClassifyResult Classify(string rootFrame)
    {
        Regex[] fold = FrameNames.CompileFoldPatterns(FrameNames.MarkerOnlyFoldPatterns);
        Dictionary<string, double> byCategory = new(StringComparer.Ordinal);
        double total = 0.0;

        foreach (SampleStack sample in _samples)
        {
            IReadOnlyList<string> frames = sample.Frames;
            bool include = FrameNames.TryFindRootStart(frames, rootFrame, out int startIdx);
            if (!include || frames.Count == 0)
            {
                continue;
            }

            total += sample.Weight;

            int leafIdx = frames.Count - 1;
            while (leafIdx > startIdx && FrameNames.IsFolded(ShortOf(frames[leafIdx]), fold))
            {
                leafIdx--;
            }

            string category = FrameCategories.Classify(ShortOf(frames[leafIdx]));
            byCategory.TryGetValue(category, out double current);
            byCategory[category] = current + sample.Weight;
        }

        List<CategoryRow> rows = new(byCategory.Count);
        foreach (KeyValuePair<string, double> entry in byCategory)
        {
            double percent = total > 0.0 ? entry.Value / total * 100.0 : 0.0;
            rows.Add(new CategoryRow(entry.Key, entry.Value, percent));
        }

        // Highest weight first; ties break by category name so the ranking is stable
        // and the JSON output deterministic.
        rows.Sort(static (a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Category, b.Category);
        });

        return new ClassifyResult(total, rootFrame, rows);
    }

    /// <summary>
    ///  Attributes each retained sample to every distinct frame on its scoped stack.
    /// </summary>
    /// <param name="rootFrame">Substring scoping the ranking to a subtree, or empty for the whole trace.</param>
    /// <param name="foldPatterns">Frame fold patterns.</param>
    /// <param name="top">Maximum number of rows to return.</param>
    /// <returns>The inclusive-time ranking.</returns>
    public RankingResult InclusiveTime(string rootFrame, IReadOnlyList<string> foldPatterns, int top)
    {
        Regex[] fold = FrameNames.CompileFoldPatterns(foldPatterns);
        Dictionary<string, double> inclTime = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        double total = 0.0;
        int contributingRecordCount = 0;

        foreach (SampleStack sample in _samples)
        {
            IReadOnlyList<string> frames = sample.Frames;
            bool include = FrameNames.TryFindRootStart(frames, rootFrame, out int startIdx);
            if (!include || frames.Count == 0)
            {
                continue;
            }

            total += sample.Weight;
            contributingRecordCount++;
            seen.Clear();

            for (int fi = startIdx; fi < frames.Count; fi++)
            {
                string name = ShortOf(frames[fi]);
                if (FrameNames.IsFolded(name, fold))
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    inclTime.TryGetValue(name, out double current);
                    inclTime[name] = current + sample.Weight;
                }
            }
        }

        return new RankingResult(
            total,
            rootFrame,
            RankRows(inclTime, total, top),
            AvailableRecordCount(contributingRecordCount));
    }

    /// <summary>
    ///  Limits a ranking's rows to those that fit
    ///  <see cref="OutputBudget.DefaultRowBudgetTokens"/>, leaving the scope totals
    ///  untouched.
    /// </summary>
    /// <param name="ranking">
    ///  The ranking to bound, as returned by <see cref="SelfTime"/> or <see cref="InclusiveTime"/>.
    /// </param>
    /// <param name="warning">The warning naming what was dropped, or <see langword="null"/> when every row fit.</param>
    /// <returns>The limited ranking, or <paramref name="ranking"/> itself when every row fit.</returns>
    /// <remarks>
    ///  <para>
    ///   Shared by both heads so they bound and word the result identically. The row cap
    ///   the caller passes to <see cref="SelfTime"/> is not a bound on response size: a
    ///   ranked row costs about 27 estimated tokens, so the response crosses the ceiling
    ///   at roughly 940 rows, which a broad trace has no trouble producing.
    ///  </para>
    ///  <para>
    ///   This is applied where a ranking becomes a response, not inside the ranking
    ///   itself, because the diff paths deliberately rank with no cap and pair the full
    ///   row sets before capping their own output.
    ///  </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="ranking"/> is <see langword="null"/>.</exception>
    public static RankingResult LimitRows(RankingResult ranking, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(ranking);

        List<RankRow> kept = OutputBudget.TakeWithinBudget(
            ranking.Rows,
            EstimateRowTokens,
            OutputBudget.DefaultRowBudgetTokens,
            out bool budgetTruncated);

        if (!budgetTruncated)
        {
            warning = null;
            return ranking;
        }

        warning =
            $"Showing {kept.Count} of {ranking.Rows.Count} ranked rows; more would exceed the "
                + $"{OutputBudget.DefaultRowBudgetTokens}-token row budget that holds the whole response under the "
                + $"{OutputBudget.DefaultCeilingTokens}-token ceiling. The scope total still covers every frame.";

        return ranking with { Rows = kept };
    }

    private static int EstimateRowTokens(RankRow row) =>
        RankRowScaffoldTokens + OutputBudget.EstimateTokens(row.Frame);

    /// <summary>
    ///  Limits a caller/callee breakdown to the rows that fit
    ///  <see cref="OutputBudget.DefaultRowBudgetTokens"/>, leaving the focus totals
    ///  untouched.
    /// </summary>
    /// <param name="callers">The breakdown to bound, as returned by <see cref="CallersOf"/>.</param>
    /// <param name="warning">The warning naming what was dropped, or <see langword="null"/> when every row fit.</param>
    /// <returns>The limited breakdown, or <paramref name="callers"/> itself when every row fit.</returns>
    /// <remarks>
    ///  <para>
    ///   Both lists share one response, so the callers are filled first and the callees
    ///   take what is left; a caller/callee view that has to drop rows keeps the side
    ///   that answers "who is calling this".
    ///  </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="callers"/> is <see langword="null"/>.</exception>
    public static CallersResult LimitRows(CallersResult callers, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(callers);

        List<CallerRow> keptCallers = OutputBudget.TakeWithinBudget(
            callers.Callers,
            EstimateCallerTokens,
            OutputBudget.DefaultRowBudgetTokens,
            out bool callersTruncated);

        int spent = 0;
        foreach (CallerRow row in keptCallers)
        {
            spent += EstimateCallerTokens(row);
        }

        bool calleesTruncated = false;
        IReadOnlyList<CalleeRow>? keptCallees = callers.Callees;
        if (callers.Callees is IReadOnlyList<CalleeRow> callees)
        {
            // The response already carries the callers, so a callee that does not fit is
            // simply dropped rather than admitted by the always-keep-the-first-row rule.
            keptCallees = OutputBudget.TakeWithinBudget(
                callees,
                EstimateCalleeTokens,
                Math.Max(0, OutputBudget.DefaultRowBudgetTokens - spent),
                out calleesTruncated,
                takeAtLeastOne: false);
        }

        if (!callersTruncated && !calleesTruncated)
        {
            warning = null;
            return callers;
        }

        warning =
            $"Showing {keptCallers.Count} of {callers.Callers.Count} callers"
                + (callers.Callees is null ? string.Empty : $" and {keptCallees!.Count} of {callers.Callees.Count} callees")
                + $"; more would exceed the {OutputBudget.DefaultRowBudgetTokens}-token row budget that holds the whole "
                + $"response under the {OutputBudget.DefaultCeilingTokens}-token ceiling. The focus totals still cover "
                + "every call site.";

        return callers with { Callers = keptCallers, Callees = keptCallees };
    }

    /// <summary>
    ///  Limits a line ranking to the rows that fit
    ///  <see cref="OutputBudget.DefaultRowBudgetTokens"/>, leaving the scope totals
    ///  untouched.
    /// </summary>
    /// <param name="lines">The ranking to bound, as returned by <see cref="HotLines"/>.</param>
    /// <param name="warning">The warning naming what was dropped, or <see langword="null"/> when every row fit.</param>
    /// <returns>The limited ranking, or <paramref name="lines"/> itself when every row fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    public static LineRankingResult LimitRows(LineRankingResult lines, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(lines);

        List<LineRow> kept = OutputBudget.TakeWithinBudget(
            lines.Rows,
            EstimateLineTokens,
            OutputBudget.DefaultRowBudgetTokens,
            out bool budgetTruncated);

        if (!budgetTruncated)
        {
            warning = null;
            return lines;
        }

        warning =
            $"Showing {kept.Count} of {lines.Rows.Count} ranked lines; more would exceed the "
                + $"{OutputBudget.DefaultRowBudgetTokens}-token row budget that holds the whole response under the "
                + $"{OutputBudget.DefaultCeilingTokens}-token ceiling. The scope total still covers every line.";

        return lines with { Rows = kept };
    }

    /// <summary>
    ///  Limits a source heat map to the lines that fit
    ///  <see cref="OutputBudget.DefaultRowBudgetTokens"/>, leaving the file totals
    ///  untouched.
    /// </summary>
    /// <param name="heatmap">The heat map to bound, as returned by <see cref="SourceHeatmap"/>.</param>
    /// <param name="warning">
    ///  The warning naming what was dropped, or <see langword="null"/> when every line fit.
    /// </param>
    /// <returns>The limited heat map, or <paramref name="heatmap"/> itself when every line fit.</returns>
    /// <remarks>
    ///  <para>
    ///   A heat map takes no row cap - its size follows the source file - so the budget
    ///   is the only bound there is. Lines are ordered for overlaying onto the source, so
    ///   a truncated map is a prefix of the file rather than its heaviest lines.
    ///  </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="heatmap"/> is <see langword="null"/>.</exception>
    public static SourceHeatmapResult LimitRows(SourceHeatmapResult heatmap, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(heatmap);

        List<HeatLine> kept = OutputBudget.TakeWithinBudget(
            heatmap.Lines,
            EstimateHeatLineTokens,
            OutputBudget.DefaultRowBudgetTokens,
            out bool budgetTruncated);

        if (!budgetTruncated)
        {
            warning = null;
            return heatmap;
        }

        warning =
            $"Showing the first {kept.Count} of {heatmap.Lines.Count} lines by line number; more would exceed the "
                + $"{OutputBudget.DefaultRowBudgetTokens}-token row budget that holds the whole response under the "
                + $"{OutputBudget.DefaultCeilingTokens}-token ceiling. The file total still covers every line; narrow "
                + "the request to a hotter file, or rank the lines instead of mapping them.";

        return heatmap with { Lines = kept };
    }

    private static int EstimateCallerTokens(CallerRow row) =>
        CallerRowScaffoldTokens + OutputBudget.EstimateTokens(row.Caller);

    private static int EstimateCalleeTokens(CalleeRow row) =>
        CallerRowScaffoldTokens + OutputBudget.EstimateTokens(row.Callee);

    private static int EstimateLineTokens(LineRow row) =>
        LineRowScaffoldTokens + OutputBudget.EstimateTokens(row.Method) + OutputBudget.EstimateTokens(row.Location);

    private static int EstimateHeatLineTokens(HeatLine row) =>
        HeatLineScaffoldTokens + OutputBudget.EstimateTokens(row.Method);

    /// <summary>
    ///  Reports the immediate callers of the topmost frame matching
    ///  <paramref name="focus"/> and, when <paramref name="includeCallees"/> is set, its
    ///  immediate callees too - a caller/callee view around one frame in a single pass.
    /// </summary>
    /// <param name="focus">Substring identifying the focus frame.</param>
    /// <param name="rootFrame">Substring scoping the analysis to a subtree, or empty for the whole trace.</param>
    /// <param name="top">Maximum number of caller (and callee) rows to return.</param>
    /// <param name="includeCallees">
    ///  Whether to also compute the focus frame's immediate callees; when
    ///  <see langword="false"/> the result's callee list is <see langword="null"/> and the
    ///  caller breakdown is identical to a callers-only query.
    /// </param>
    /// <returns>The caller breakdown, with the callee breakdown when requested.</returns>
    /// <remarks>
    ///  <para>
    ///   Both directions read the same deepest occurrence of the focus frame in each
    ///   sample, so the caller and callee lists partition the same focus-inclusive weight.
    ///   The focus frame's own self-time - the sample landing in the focus frame itself, or
    ///   in the trailing run of folded artifacts below it (a JIT-helper leaf or the
    ///   synthetic <c>CPU_TIME</c> marker) - is credited to <c>&lt;self&gt;</c>, the same
    ///   frames the self-time ranking folds. Folding only that trailing run means a real
    ///   callee whose name merely matches a fold pattern is never hidden as self-time.
    ///  </para>
    /// </remarks>
    public CallersResult CallersOf(string focus, string rootFrame, int top, bool includeCallees = false)
    {
        Regex[]? fold = includeCallees ? FrameNames.CompileFoldPatterns(FrameNames.DefaultFoldPatterns) : null;
        Dictionary<string, double> callerTime = new(StringComparer.Ordinal);
        Dictionary<string, double>? calleeTime = includeCallees ? new(StringComparer.Ordinal) : null;
        double targetTotal = 0.0;
        double total = 0.0;
        int contributingRecordCount = 0;

        foreach (SampleStack sample in _samples)
        {
            IReadOnlyList<string> frames = sample.Frames;
            bool include = FrameNames.TryFindRootStart(frames, rootFrame, out int startIdx);
            if (!include || frames.Count == 0)
            {
                continue;
            }

            total += sample.Weight;

            // For the callee side, the real leaf is the deepest non-folded frame; every
            // frame below it is a folded self-time artifact (the CPU_TIME marker or a
            // JIT-helper leaf). Folding only this trailing run - the same frames the
            // self-time ranking folds - keeps a real callee whose name merely matches a
            // fold pattern (say it contains WriteBarrier or JIT_) from being hidden as
            // self-time when it has real frames beneath it.
            int realLeaf = frames.Count - 1;
            if (includeCallees)
            {
                while (realLeaf > startIdx && FrameNames.IsFolded(ShortOf(frames[realLeaf]), fold!))
                {
                    realLeaf--;
                }
            }

            for (int si = frames.Count - 1; si >= startIdx; si--)
            {
                string name = ShortOf(frames[si]);
                if (!name.Contains(focus, StringComparison.Ordinal))
                {
                    continue;
                }

                targetTotal += sample.Weight;
                contributingRecordCount++;
                string caller = si > startIdx ? ShortOf(frames[si - 1]) : "<root>";
                callerTime.TryGetValue(caller, out double currentCaller);
                callerTime[caller] = currentCaller + sample.Weight;

                if (includeCallees)
                {
                    // At or below the real leaf the focus frame is executing itself - its
                    // time is self-time; above it, the next frame down is the real callee.
                    string callee = si >= realLeaf ? "<self>" : ShortOf(frames[si + 1]);
                    calleeTime!.TryGetValue(callee, out double currentCallee);
                    calleeTime[callee] = currentCallee + sample.Weight;
                }

                break;
            }
        }

        List<CallerRow> callerRows = [];
        foreach (KeyValuePair<string, double> pair in callerTime)
        {
            double pct = targetTotal > 0 ? 100.0 * pair.Value / targetTotal : 0.0;
            callerRows.Add(new CallerRow(pair.Key, pair.Value, pct));
        }

        // Break ties by caller name so the ordering is deterministic across runs and machines.
        callerRows.Sort(static (a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Caller, b.Caller);
        });

        if (callerRows.Count > top)
        {
            callerRows.RemoveRange(top, callerRows.Count - top);
        }

        IReadOnlyList<CalleeRow>? calleeRows = null;
        if (includeCallees)
        {
            List<CalleeRow> callees = [];
            foreach (KeyValuePair<string, double> pair in calleeTime!)
            {
                double pct = targetTotal > 0 ? 100.0 * pair.Value / targetTotal : 0.0;
                callees.Add(new CalleeRow(pair.Key, pair.Value, pct));
            }

            // Break ties by callee name so the ordering is deterministic across runs.
            callees.Sort(static (a, b) =>
            {
                int byWeight = b.Weight.CompareTo(a.Weight);
                return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Callee, b.Callee);
            });

            if (callees.Count > top)
            {
                callees.RemoveRange(top, callees.Count - top);
            }

            calleeRows = callees;
        }

        double pctOfScope = total > 0 ? 100.0 * targetTotal / total : 0.0;
        return new CallersResult(
            focus,
            targetTotal,
            pctOfScope,
            total,
            callerRows,
            calleeRows,
            AvailableRecordCount(contributingRecordCount));
    }

    /// <summary>
    ///  Computes the line-level self-time ranking: each leaf sample (after
    ///  folding JIT-helper leaves into their caller) is attributed to the source
    ///  line that was executing, scoped to the methods whose shortened name
    ///  contains <paramref name="methodFilter"/>.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Only samples carrying per-frame source locations contribute; speedscope
    ///   inputs have none, so this ranking is meaningful only for
    ///   <c>.nettrace</c> and <c>.etl</c> traces read with local PDBs present.
    ///  </para>
    /// </remarks>
    /// <param name="methodFilter">Substring scoping to matching methods, or empty for every method.</param>
    /// <param name="foldPatterns">Leaf-frame fold patterns.</param>
    /// <param name="top">Maximum number of rows to return.</param>
    /// <returns>The line-level self-time ranking.</returns>
    public LineRankingResult HotLines(string methodFilter, IReadOnlyList<string> foldPatterns, int top)
    {
        Regex[] fold = FrameNames.CompileFoldPatterns(foldPatterns);
        Dictionary<string, (double Ms, string Method, string Location)> lineTime = new(StringComparer.Ordinal);
        double total = 0.0;
        int attributedRecordCount = 0;
        int unattributedRecordCount = 0;

        foreach (SampleStack sample in _samples)
        {
            IReadOnlyList<string> frames = sample.Frames;
            if (frames.Count == 0)
            {
                continue;
            }

            int leafIdx = frames.Count - 1;
            while (leafIdx > 0 && FrameNames.IsFolded(ShortOf(frames[leafIdx]), fold))
            {
                leafIdx--;
            }

            string method = ShortOf(frames[leafIdx]);
            if (methodFilter.Length > 0 && !method.Contains(methodFilter, StringComparison.Ordinal))
            {
                continue;
            }

            IReadOnlyList<string>? locations = sample.FrameLocations;
            if (locations is null)
            {
                unattributedRecordCount++;
                continue;
            }

            total += sample.Weight;

            bool attributed = leafIdx < locations.Count && locations[leafIdx].Length > 0;
            string location = attributed ? locations[leafIdx] : "<no source>";
            if (attributed)
            {
                attributedRecordCount++;
            }
            else
            {
                unattributedRecordCount++;
            }

            string key = $"{method}\u0000{location}";
            lineTime.TryGetValue(key, out (double Ms, string Method, string Location) current);
            lineTime[key] = (current.Ms + sample.Weight, method, location);
        }

        List<LineRow> rows = [];
        foreach (KeyValuePair<string, (double Ms, string Method, string Location)> pair in lineTime)
        {
            double pct = total > 0 ? 100.0 * pair.Value.Ms / total : 0.0;
            rows.Add(new LineRow(pair.Value.Method, pair.Value.Location, pair.Value.Ms, pct));
        }

        // Break ties by source location, then method, so the ordering is fully
        // deterministic even when two methods map to the same file:line with equal time.
        rows.Sort(static (a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            if (byWeight != 0)
            {
                return byWeight;
            }

            int byLocation = string.CompareOrdinal(a.Location, b.Location);
            return byLocation != 0 ? byLocation : string.CompareOrdinal(a.Method, b.Method);
        });

        if (rows.Count > top)
        {
            rows.RemoveRange(top, rows.Count - top);
        }

        return new LineRankingResult(
            total,
            methodFilter,
            rows,
            AvailableRecordCount(attributedRecordCount),
            AvailableRecordCount(unattributedRecordCount));
    }

    /// <summary>
    ///  Computes a per-line self-time heat map for a single source file: each leaf
    ///  sample (after folding JIT-helper leaves into their caller) whose executing
    ///  source line belongs to <paramref name="fileName"/> is bucketed by line
    ///  number, ordered by line for overlaying onto the source.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Matching is by file name only (the trace records the build-time file name,
    ///   not its full path), so two source files that share a name are merged. The
    ///   percent on each line is the share of whole-trace time, making absolute
    ///   hotness comparable across files.
    ///  </para>
    /// </remarks>
    /// <param name="fileName">The source file name to build the heat map for (no directory).</param>
    /// <param name="foldPatterns">Leaf-frame fold patterns.</param>
    /// <returns>The per-line heat map, ordered by line number.</returns>
    public SourceHeatmapResult SourceHeatmap(string fileName, IReadOnlyList<string> foldPatterns)
    {
        Regex[] fold = FrameNames.CompileFoldPatterns(foldPatterns);
        Dictionary<int, LineAccumulator> lines = new();
        double traceTotal = 0.0;
        double fileTotal = 0.0;
        string matchedFile = fileName;
        int attributedRecordCount = 0;
        int unattributedRecordCount = 0;

        foreach (SampleStack sample in _samples)
        {
            IReadOnlyList<string> frames = sample.Frames;
            if (frames.Count == 0)
            {
                continue;
            }

            traceTotal += sample.Weight;

            IReadOnlyList<string>? locations = sample.FrameLocations;
            if (locations is null)
            {
                unattributedRecordCount++;
                continue;
            }

            int leafIdx = frames.Count - 1;
            while (leafIdx > 0 && FrameNames.IsFolded(ShortOf(frames[leafIdx]), fold))
            {
                leafIdx--;
            }

            if (leafIdx >= locations.Count
                || !TrySplitLocation(locations[leafIdx], out string leafFile, out int line))
            {
                unattributedRecordCount++;
                continue;
            }

            if (!string.Equals(leafFile, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Preserve the file name's casing as recorded in the trace.
            matchedFile = leafFile;
            fileTotal += sample.Weight;
            attributedRecordCount++;

            if (!lines.TryGetValue(line, out LineAccumulator? accumulator))
            {
                accumulator = new LineAccumulator();
                lines[line] = accumulator;
            }

            accumulator.Add(sample.Weight, ShortOf(frames[leafIdx]));
        }

        List<HeatLine> rows = new(lines.Count);
        foreach (KeyValuePair<int, LineAccumulator> pair in lines)
        {
            LineAccumulator accumulator = pair.Value;
            double pct = traceTotal > 0 ? 100.0 * accumulator.Weight / traceTotal : 0.0;
            rows.Add(new HeatLine(pair.Key, accumulator.Weight, pct, accumulator.SampleCount, accumulator.DominantMethod));
        }

        rows.Sort(static (a, b) => a.Line.CompareTo(b.Line));
        return new SourceHeatmapResult(
            traceTotal,
            matchedFile,
            fileTotal,
            rows,
            AvailableRecordCount(attributedRecordCount),
            AvailableRecordCount(unattributedRecordCount));
    }

    /// <summary>
    ///  Builds a top-down call tree over the scoped samples: each node's children
    ///  are the frames it called, weighted by the metric spent in them.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Folded frames are skipped so the tree shows only real methods, mirroring
    ///   inclusive-time. The tree is bounded two ways so it stays readable and within
    ///   an agent's token budget: <paramref name="maxDepth"/> caps how far below the
    ///   root it descends, and <paramref name="minPercentOfScope"/> prunes branches
    ///   whose share of the scoped total is too small to matter.
    ///  </para>
    /// </remarks>
    /// <param name="rootFrame">Substring scoping the tree to a subtree, or empty for the whole trace.</param>
    /// <param name="foldPatterns">Frame fold patterns; folded frames are skipped.</param>
    /// <param name="maxDepth">
    ///  The maximum number of frame levels below the root to descend. Must be in <c>[0, <see cref="MaxTreeDepth"/>]</c>.
    /// </param>
    /// <param name="minPercentOfScope">
    ///  The minimum share of the scoped total, in percent, a node must have to appear. Must be non-negative.
    /// </param>
    /// <returns>The call tree.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///  <paramref name="maxDepth"/> is negative or greater than <see cref="MaxTreeDepth"/>, or
    ///  <paramref name="minPercentOfScope"/> is negative.
    /// </exception>
    public CallTreeResult CallTree(
        string rootFrame,
        IReadOnlyList<string> foldPatterns,
        int maxDepth,
        double minPercentOfScope)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDepth, MaxTreeDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(minPercentOfScope);

        Regex[] fold = FrameNames.CompileFoldPatterns(foldPatterns);
        TreeBuilder root = new("<root>");
        double total = 0.0;

        foreach (SampleStack sample in _samples)
        {
            IReadOnlyList<string> frames = sample.Frames;
            bool include = FrameNames.TryFindRootStart(frames, rootFrame, out int startIdx);
            if (!include || frames.Count == 0)
            {
                continue;
            }

            total += sample.Weight;
            root.Weight += sample.Weight;

            TreeBuilder node = root;
            int depth = 0;
            for (int fi = startIdx; fi < frames.Count && depth < maxDepth; fi++)
            {
                string name = ShortOf(frames[fi]);
                if (FrameNames.IsFolded(name, fold))
                {
                    continue;
                }

                node = node.Child(name);
                node.Weight += sample.Weight;
                depth++;
            }
        }

        return new CallTreeResult(total, rootFrame, BuildTreeNode(root, total, minPercentOfScope));
    }

    /// <summary>
    ///  Converts a mutable <see cref="TreeBuilder"/> into the immutable
    ///  <see cref="TreeNode"/>, pruning children below the threshold and ordering
    ///  the survivors by weight (ordinal name as the deterministic tiebreak).
    /// </summary>
    private static TreeNode BuildTreeNode(TreeBuilder node, double scopeTotal, double minPercentOfScope)
    {
        double percent = scopeTotal > 0 ? 100.0 * node.Weight / scopeTotal : 0.0;
        if (node.Children is null)
        {
            return new TreeNode(node.Frame, node.Weight, percent, []);
        }

        List<TreeNode> children = [];
        foreach (TreeBuilder child in node.Children.Values)
        {
            double childPercent = scopeTotal > 0 ? 100.0 * child.Weight / scopeTotal : 0.0;
            if (childPercent < minPercentOfScope)
            {
                continue;
            }

            children.Add(BuildTreeNode(child, scopeTotal, minPercentOfScope));
        }

        children.Sort(static (a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Frame, b.Frame);
        });

        return new TreeNode(node.Frame, node.Weight, percent, children);
    }

    /// <summary>
    ///  Splits a <c>file:line</c> location into its file name and line number.
    ///  Returns <see langword="false"/> for empty, unresolved (<c>&lt;no source&gt;</c>)
    ///  or otherwise malformed locations.
    /// </summary>
    private static bool TrySplitLocation(string location, out string file, out int line)
    {
        file = "";
        line = 0;
        if (location.Length == 0)
        {
            return false;
        }

        int colon = location.LastIndexOf(':');
        if (colon <= 0 || colon == location.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(location.AsSpan(colon + 1), out line))
        {
            return false;
        }

        // Line numbers are 1-based; reject zero and negative values that cannot map onto source.
        if (line < 1)
        {
            line = 0;
            return false;
        }

        file = location[..colon];
        return true;
    }

    private static List<RankRow> RankRows(Dictionary<string, double> times, double total, int top)
    {
        List<RankRow> rows = [];
        foreach (KeyValuePair<string, double> pair in times)
        {
            double pct = total > 0 ? 100.0 * pair.Value / total : 0.0;
            rows.Add(new RankRow(pair.Key, pair.Value, pct));
        }

        // Break ties by frame name so the ordering is deterministic across runs and machines.
        rows.Sort(static (a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Frame, b.Frame);
        });

        if (rows.Count > top)
        {
            rows.RemoveRange(top, rows.Count - top);
        }

        return rows;
    }

    private int? AvailableRecordCount(int count) =>
        _source.RecordSemantics == StackRecordSemantics.Unavailable ? null : count;
}
