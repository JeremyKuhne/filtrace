// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics.CodeAnalysis;
using Filtrace.Output;
using Filtrace.Tracing.Readers;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The process lifecycle provider: reads the Windows kernel's process start and stop
///  events, and the image loads under them, into a <see cref="LifecycleResult"/> that
///  splits each invocation's wall clock into loader, child, and teardown phases.
/// </summary>
/// <remarks>
///  <para>
///   Sampled CPU cannot answer where a 50 ms command went. A parent that launches a
///   child and waits owns no samples while it waits, so a CPU ranking reports the
///   child's work and silently drops the wall clock around it. Kernel process events
///   carry the wall clock directly, which is what this reads.
///  </para>
///  <para>
///   Process start and stop are ETW kernel events, so this needs a Windows
///   <c>.etl</c>; an EventPipe <c>.nettrace</c> carries no equivalent, and the caller
///   is expected to reject one before calling.
///  </para>
/// </remarks>
public sealed class LifecycleProvider
{
    // The JSON scaffolding around one invocation - the phase fields and the wrapper -
    // and around each process inside it, which the per-row estimate adds to the
    // process names. An invocation carries its root plus every child, so its cost is
    // driven by the child count rather than by anything the caller passes.
    private const int InvocationScaffoldTokens = 44;
    private const int ProcessScaffoldTokens = 55;
    /// <summary>
    ///  The largest number of root invocations reported. A capture matrix runs tens of
    ///  invocations; a name that matches a busy system host could otherwise match
    ///  thousands and turn a report into a dump.
    /// </summary>
    public const int MaxInvocations = 500;

    /// <summary>
    ///  The largest number of descendants reported per invocation.
    /// </summary>
    public const int MaxChildrenPerInvocation = 50;

    /// <summary>
    ///  Reads the lifecycle report from the ETW trace at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The <c>.etl</c> file path.</param>
    /// <param name="scope">
    ///  Which processes are the invocation roots. The selector chooses roots only;
    ///  descendants are always resolved from the trace, so
    ///  <see cref="ScopeRequest.IncludeChildren"/> is not consulted. Pass
    ///  <see langword="null"/> to select the busiest process by name.
    /// </param>
    /// <param name="images">
    ///  Case-insensitive module-name substrings to time as loader milestones, or empty
    ///  for none.
    /// </param>
    /// <param name="warnings">Collects advisories about how the selector matched, or <see langword="null"/> to discard them.</param>
    /// <returns>The lifecycle report; an empty report when the selector matches nothing.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty, or a requested process id was reused.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public LifecycleResult Read(
        string path,
        ScopeRequest? scope = null,
        IReadOnlyList<string>? images = null,
        List<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        using TraceLog traceLog = TraceConverter.OpenTraceLog(fullPath, out _);

        if (!TryResolveRootSelector(traceLog, scope, out ProcessSelector? selector))
        {
            return Empty(string.Empty);
        }

        // Roots only: an invocation is one root process instance, and its descendants are
        // walked from the trace's parent links below. Resolving with children here would
        // fold every child into the root set and report each one as its own invocation.
        HashSet<int> rootIds = ProcessTree.ResolvePids(
            traceLog,
            new ProcessScope(selector, IncludeChildren: false),
            warnings);

        string scopeLabel = Describe(selector);
        if (rootIds.Count == 0)
        {
            return Empty(scopeLabel);
        }

        List<TraceProcess> roots = [];
        foreach (TraceProcess process in traceLog.Processes)
        {
            // The resolved set is process ids, which cannot separate an id from a later,
            // unrelated process that reused it - so the selector's own identity test is
            // reapplied per instance before an instance becomes an invocation.
            if (rootIds.Contains(process.ProcessID) && Matches(process, selector))
            {
                roots.Add(process);
            }
        }

        roots.Sort(static (left, right) => left.StartTimeRelativeMsec.CompareTo(right.StartTimeRelativeMsec));
        if (roots.Count > MaxInvocations)
        {
            warnings?.Add(
                $"The selector matched {roots.Count} invocations; reporting the first {MaxInvocations} in start order.");
            roots = roots[..MaxInvocations];
        }

        Dictionary<ProcessIndex, List<TraceProcess>> childrenByRoot = MapDescendants(traceLog, roots);

        double sessionEndMs = traceLog.SessionEndTimeRelativeMSec;
        List<LifecycleInvocation> invocations = new(roots.Count);
        double totalRootCpuMs = 0;
        double totalChildCpuMs = 0;
        int cappedInvocations = 0;

        for (int index = 0; index < roots.Count; index++)
        {
            TraceProcess root = roots[index];
            List<TraceProcess> descendants = childrenByRoot.GetValueOrDefault(root.ProcessIndex) ?? [];
            descendants.Sort(static (left, right) => left.StartTimeRelativeMsec.CompareTo(right.StartTimeRelativeMsec));

            totalRootCpuMs += root.CPUMSec;
            foreach (TraceProcess child in descendants)
            {
                totalChildCpuMs += child.CPUMSec;
            }

            // The phases span every descendant even when the reported list is capped: the
            // last child to stop may be one the cap dropped, and a child span measured
            // from a truncated set would understate the invocation.
            List<TraceProcess> reported = descendants;
            if (descendants.Count > MaxChildrenPerInvocation)
            {
                reported = descendants[..MaxChildrenPerInvocation];
                cappedInvocations++;
            }

            invocations.Add(BuildInvocation(index + 1, root, descendants, reported, sessionEndMs));
        }

        if (cappedInvocations > 0)
        {
            warnings?.Add(
                $"{cappedInvocations} invocation(s) launched more than {MaxChildrenPerInvocation} descendants; "
                + "the listed children are capped in start order, and the phases still span all of them.");
        }

        int measuredCount = invocations.Count(static invocation => invocation.Measurable);
        return new LifecycleResult(
            scopeLabel,
            invocations.Count,
            measuredCount,
            totalRootCpuMs,
            totalChildCpuMs,
            SummarizePhases(invocations),
            invocations,
            SummarizeImages(roots, childrenByRoot, images));
    }

    private static LifecycleResult Empty(string scope) =>
        new(scope, 0, 0, 0, 0, [], [], []);

    /// <summary>
    ///  Limits a report's per-invocation detail to the invocations that fit both
    ///  <paramref name="top"/> and <see cref="OutputBudget.DefaultRowBudgetTokens"/>,
    ///  leaving the phase medians untouched.
    /// </summary>
    /// <param name="report">The full report, as returned by <see cref="Read"/>.</param>
    /// <param name="top">
    ///  The caller's maximum detail row count. Must be non-negative; zero keeps the
    ///  phase medians and drops every invocation row.
    /// </param>
    /// <param name="warning">
    ///  The warning naming what was dropped, or <see langword="null"/> when every
    ///  invocation was kept.
    /// </param>
    /// <returns>
    ///  The limited report, or <paramref name="report"/> itself when every invocation fit.
    /// </returns>
    /// <remarks>
    ///  <para>
    ///   Invocations stay in start order, because a lifecycle report is read as a
    ///   sequence. One invocation carries its root process plus every child it launched,
    ///   so a wide command matrix reaches the budget on row size as well as row count.
    ///  </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is negative.</exception>
    public static LifecycleResult LimitDetail(LifecycleResult report, int top, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfNegative(top);

        List<LifecycleInvocation> kept = OutputBudget.TakeWithinBudget(
            report.Invocations.Take(top),
            EstimateInvocationTokens,
            OutputBudget.DefaultRowBudgetTokens,
            out bool budgetTruncated);

        if (kept.Count == report.Invocations.Count)
        {
            warning = null;
            return report;
        }

        warning = budgetTruncated
            ? $"Showing {kept.Count} of {report.InvocationCount} invocations in start order; more would exceed "
                + $"the {OutputBudget.DefaultRowBudgetTokens}-token detail budget that holds the whole response "
                + $"under the {OutputBudget.DefaultCeilingTokens}-token ceiling. The medians still cover all of them."
            : top == 0
                ? $"Aggregate only: {report.InvocationCount} invocations were not listed; the medians cover all "
                    + "of them. Ask again with a positive top for the per-invocation detail."
                : $"Showing the first {top} of {report.InvocationCount} invocations in start order; "
                    + "the medians cover all of them.";

        return report with { Invocations = kept };
    }

    private static int EstimateInvocationTokens(LifecycleInvocation invocation)
    {
        int tokens = InvocationScaffoldTokens + EstimateProcessTokens(invocation.Root);
        foreach (LifecycleProcess child in invocation.Children)
        {
            tokens += EstimateProcessTokens(child);
        }

        return tokens;
    }

    private static int EstimateProcessTokens(LifecycleProcess process) =>
        ProcessScaffoldTokens + OutputBudget.EstimateTokens(process.Name);

    /// <summary>
    ///  Describes how much of <paramref name="result"/> is measurable, as the warnings
    ///  both heads report: whether a selector resolved at all, whether it matched, and
    ///  how many matched invocations the capture observed end to end.
    /// </summary>
    /// <param name="result">The lifecycle report to describe.</param>
    /// <returns>The coverage warnings; empty when every invocation is fully observed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <remarks>
    ///  <para>
    ///   Shared rather than duplicated per head: the CLI and the MCP tool must not drift
    ///   on what an empty or clipped report means, and the two failures read very
    ///   differently - an unresolved selector is a capture problem, an unmatched one is a
    ///   scope problem.
    ///  </para>
    /// </remarks>
    public static IReadOnlyList<string> DescribeCoverage(LifecycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.InvocationCount == 0)
        {
            return string.IsNullOrEmpty(result.Scope)
                ? [
                    "The trace carries no process the report could use as an invocation root. "
                    + "Check that the capture enabled the Process kernel keyword and that it "
                    + "recorded CPU samples for a named process."
                ]
                : [$"No process matching '{result.Scope}' was found in the trace."];
        }

        if (result.MeasuredCount == 0)
        {
            return [
                "No invocation had both its start and its stop recorded, so no phase medians are "
                + "reported; every lifetime shown is a lower bound clipped to the capture window."
            ];
        }

        return result.MeasuredCount < result.InvocationCount
            ? [
                $"{result.InvocationCount - result.MeasuredCount} of {result.InvocationCount} invocations were "
                + "clipped to the capture window and are excluded from the phase medians."
            ]
            : [];
    }

    // The selector that chooses invocation roots. An explicit selector wins; otherwise
    // the busiest process's name stands in, matching how every other verb auto-scopes.
    private static bool TryResolveRootSelector(
        TraceLog traceLog,
        ScopeRequest? scope,
        [NotNullWhen(true)] out ProcessSelector? selector)
    {
        selector = scope?.Selector;
        if (selector is not null)
        {
            return true;
        }

        if (ProcessTree.FindBusiestProcessName(traceLog) is string busiest)
        {
            selector = new ProcessNameSelector(busiest);
            return true;
        }

        return false;
    }

    private static string Describe(ProcessSelector selector) => selector is ProcessIdSelector ids
        ? $"pids {string.Join(", ", ids.ProcessIds)}"
        : ((ProcessNameSelector)selector).NameSubstring;

    // Whether a process instance satisfies the selector in its own right. An id selector
    // needs no test: ProcessTree refuses a requested id that more than one process in the
    // trace carries, so a surviving id identifies exactly one instance.
    private static bool Matches(TraceProcess process, ProcessSelector selector) => selector switch
    {
        ProcessNameSelector name => process.Name is not null
            && process.Name.Contains(name.NameSubstring, StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    // Group every process under the root instance it descends from. Keying on
    // ProcessIndex rather than the process id keeps invocations apart when a capture
    // matrix reuses ids across runs.
    private static Dictionary<ProcessIndex, List<TraceProcess>> MapDescendants(
        TraceLog traceLog,
        List<TraceProcess> roots)
    {
        HashSet<ProcessIndex> rootIndexes = [.. roots.Select(static root => root.ProcessIndex)];
        Dictionary<ProcessIndex, List<TraceProcess>> descendants = [];

        foreach (TraceProcess process in traceLog.Processes)
        {
            if (rootIndexes.Contains(process.ProcessIndex))
            {
                continue;
            }

            // The parent chain is shallow (host -> apphost -> worker), so walking it per
            // process costs less than materializing a child index.
            for (TraceProcess? ancestor = process.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (rootIndexes.Contains(ancestor.ProcessIndex))
                {
                    if (!descendants.TryGetValue(ancestor.ProcessIndex, out List<TraceProcess>? list))
                    {
                        list = [];
                        descendants[ancestor.ProcessIndex] = list;
                    }

                    list.Add(process);
                    break;
                }
            }
        }

        return descendants;
    }

    private static LifecycleInvocation BuildInvocation(
        int ordinal,
        TraceProcess root,
        List<TraceProcess> allDescendants,
        List<TraceProcess> reportedDescendants,
        double sessionEndMs)
    {
        LifecycleProcess rootRecord = Describe(root, sessionEndMs);
        List<LifecycleProcess> children = [.. reportedDescendants.Select(child => Describe(child, sessionEndMs))];

        double? startToChild = null;
        double? childSpan = null;
        double? childToStop = null;

        if (allDescendants.Count > 0)
        {
            double firstChildStart = allDescendants.Min(static child => child.StartTimeRelativeMsec);
            double lastChildStop = allDescendants.Max(static child => child.EndTimeRelativeMsec);
            startToChild = firstChildStart - rootRecord.StartMs;
            childSpan = lastChildStop - firstChildStart;
            childToStop = rootRecord.StopMs - lastChildStop;
        }

        bool measurable = rootRecord.StartObserved && rootRecord.StopObserved;
        return new LifecycleInvocation(
            ordinal,
            rootRecord,
            children,
            startToChild,
            childSpan,
            childToStop,
            measurable);
    }

    private static LifecycleProcess Describe(TraceProcess process, double sessionEndMs)
    {
        // TraceEvent clips a process it did not see start to the capture start, and one it
        // did not see exit to the capture end. Both clipped edges make the lifetime a lower
        // bound, so they are reported rather than silently averaged into a median. The
        // recorded exit status is the stronger stop signal: it exists only when a
        // Process/Stop was decoded.
        double startMs = process.StartTimeRelativeMsec;
        double stopMs = process.EndTimeRelativeMsec;
        bool startObserved = startMs > 0;
        bool stopObserved = process.ExitStatus is not null || stopMs < sessionEndMs;

        return new LifecycleProcess(
            process.ProcessID,
            string.IsNullOrEmpty(process.Name) ? "(unknown)" : process.Name,
            startMs,
            stopMs,
            stopMs - startMs,
            process.CPUMSec,
            startObserved,
            stopObserved,
            process.ExitStatus);
    }

    private static IReadOnlyList<LifecyclePhase> SummarizePhases(List<LifecycleInvocation> invocations)
    {
        List<LifecycleInvocation> measured = [.. invocations.Where(static invocation => invocation.Measurable)];
        if (measured.Count == 0)
        {
            return [];
        }

        List<LifecyclePhase> phases = [];
        AddPhase(phases, "root lifetime", [.. measured.Select(static invocation => invocation.Root.LifetimeMs)]);
        AddPhase(phases, "root start to first child", Collect(measured, static invocation => invocation.RootStartToChildStartMs));
        AddPhase(phases, "child span", Collect(measured, static invocation => invocation.ChildSpanMs));
        AddPhase(phases, "last child stop to root stop", Collect(measured, static invocation => invocation.ChildStopToRootStopMs));
        return phases;
    }

    private static List<double> Collect(
        List<LifecycleInvocation> invocations,
        Func<LifecycleInvocation, double?> select)
    {
        List<double> values = [];
        foreach (LifecycleInvocation invocation in invocations)
        {
            if (select(invocation) is double value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static void AddPhase(List<LifecyclePhase> phases, string name, List<double> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        values.Sort();
        phases.Add(new LifecyclePhase(name, values.Count, Median(values), values[0], values[^1]));
    }

    // A true p50 over a list the caller has already sorted.
    private static double Median(List<double> sorted)
    {
        int middle = sorted.Count / 2;
        return (sorted.Count % 2) == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static IReadOnlyList<LifecycleImageMilestone> SummarizeImages(
        List<TraceProcess> roots,
        Dictionary<ProcessIndex, List<TraceProcess>> childrenByRoot,
        IReadOnlyList<string>? images)
    {
        if (images is not { Count: > 0 })
        {
            return [];
        }

        Dictionary<string, List<double>> offsets = new(StringComparer.OrdinalIgnoreCase);
        foreach (TraceProcess root in roots)
        {
            if (root.StartTimeRelativeMsec <= 0)
            {
                continue;
            }

            List<TraceProcess> tree = [root, .. childrenByRoot.GetValueOrDefault(root.ProcessIndex) ?? []];
            foreach (string image in images)
            {
                if (string.IsNullOrWhiteSpace(image))
                {
                    continue;
                }

                if (FindFirstLoad(tree, image) is not double loadMs)
                {
                    continue;
                }

                if (!offsets.TryGetValue(image, out List<double>? values))
                {
                    values = [];
                    offsets[image] = values;
                }

                values.Add(loadMs - root.StartTimeRelativeMsec);
            }
        }

        List<LifecycleImageMilestone> milestones = new(offsets.Count);
        foreach ((string image, List<double> values) in offsets)
        {
            values.Sort();
            milestones.Add(new LifecycleImageMilestone(image, values.Count, Median(values), values[0], values[^1]));
        }

        milestones.Sort(static (left, right) => left.MedianOffsetMs.CompareTo(right.MedianOffsetMs));
        return milestones;

        static double? FindFirstLoad(List<TraceProcess> tree, string image)
        {
            double? earliest = null;
            foreach (TraceProcess process in tree)
            {
                foreach (TraceLoadedModule module in process.LoadedModules)
                {
                    string name = module.Name ?? string.Empty;
                    if (!name.Contains(image, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    double loadMs = module.LoadTimeRelativeMSec;
                    if (loadMs > 0 && (earliest is null || loadMs < earliest))
                    {
                        earliest = loadMs;
                    }
                }
            }

            return earliest;
        }
    }
}
