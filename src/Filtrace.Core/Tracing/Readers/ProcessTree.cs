// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Resolves a <see cref="ProcessScope"/> against a trace's process table into the
///  set of process IDs that make up the scoped workload tree.
/// </summary>
/// <remarks>
///  <para>
///   Both the CPU stack reader and the thread-time provider scope a machine-wide
///   capture to one workload, and both need the same rule: the processes whose
///   name matches plus, by default, all of their descendants. Keeping that rule in
///   one place means the two paths cannot drift.
///  </para>
/// </remarks>
internal static class ProcessTree
{
    /// <summary>
    ///  Resolves a high-level <see cref="ScopeRequest"/> against a trace's process
    ///  table into the set of process IDs to keep, applying the automatic
    ///  busiest-process default when neither an explicit selector nor the all-processes
    ///  opt-out was given.
    /// </summary>
    /// <param name="traceLog">The opened trace whose process table is queried.</param>
    /// <param name="request">The scope intent to resolve.</param>
    /// <returns>
    ///  What the request resolved to: the process IDs to keep (<see langword="null"/>
    ///  when every process is read), how to name the scope, and any advisories about
    ///  how the selector matched.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///  A requested process id was reused by more than one process in the trace.
    /// </exception>
    public static ScopeResolution ResolveScope(TraceLog traceLog, ScopeRequest request)
    {
        if (request.IncludeAll)
        {
            return ScopeResolution.Unscoped;
        }

        // An explicit selector wins; otherwise the automatic default picks the busiest
        // process so a machine-wide capture narrows to the workload without the caller
        // naming it. A capture with no busy named process leaves the read unscoped.
        bool automatic = request.Selector is null;
        ProcessSelector? selector = request.Selector;
        if (selector is null)
        {
            if (FindBusiestProcessName(traceLog) is not string busiest)
            {
                return ScopeResolution.Unscoped;
            }

            selector = ProcessNameSelector.FromTraceName(busiest);
        }

        List<string> warnings = [];
        HashSet<int> roots = ResolveRoots(traceLog, selector, warnings);
        HashSet<int> keep = request.IncludeChildren
            ? IncludeDescendants(traceLog, roots)
            : roots;
        AppliedProcessScope appliedScope = CreateAppliedScope(
            automatic,
            selector,
            roots,
            keep,
            request.IncludeChildren);

        // An explicit selector always reports (the caller asked to scope, even if it
        // happens to match every process). The automatic scope only reports when it
        // actually narrowed - a capture that is already a single tree (a trimmed
        // fixture, say) is not "scoped" in any meaningful sense, so it stays silent
        // rather than emit a notice, and its advisories with it, for a no-op.
        return !automatic || NarrowsTheCapture(traceLog, keep)
            ? new ScopeResolution(
                keep,
                Label(selector),
                Phrase(selector, request.IncludeChildren),
                warnings,
                appliedScope)
            : new ScopeResolution(keep, null, null, [], appliedScope);
    }

    // A short identity for the scope, for structured output and terse rendering.
    private static string Label(ProcessSelector selector) => selector is ProcessIdSelector ids
        ? FormatIds(ids.ProcessIds)
        : ((ProcessNameSelector)selector).NameSubstring;

    // The scope as a prose phrase, so a warning reads the same whichever selector and
    // descendant mode produced it.
    private static string Phrase(ProcessSelector selector, bool includeChildren)
    {
        if (selector is ProcessIdSelector ids)
        {
            return includeChildren
                ? $"the process tree of {FormatIds(ids.ProcessIds)}"
                : $"{FormatIds(ids.ProcessIds)} (no children)";
        }

        string name = ((ProcessNameSelector)selector).NameSubstring;
        return includeChildren
            ? $"the '{name}' process tree"
            : $"the '{name}' process itself (no children)";
    }

    // The investigation shape this exists for launches tens of processes, so an
    // unbounded id list would dominate every warning it appears in.
    private const int MaxRenderedIds = 8;

    private static string FormatIds(IReadOnlyList<int> processIds)
    {
        string noun = processIds.Count == 1 ? "pid" : "pids";
        if (processIds.Count <= MaxRenderedIds)
        {
            return $"{noun} {string.Join(", ", processIds)}";
        }

        return $"{noun} {string.Join(", ", processIds.Take(MaxRenderedIds))} and {processIds.Count - MaxRenderedIds} more";
    }

    // Whether the kept set excludes at least one process that carried activity, i.e.
    // scoping actually dropped something rather than matching the whole capture.
    private static bool NarrowsTheCapture(TraceLog traceLog, HashSet<int> keep)
    {
        foreach (TraceProcess process in traceLog.Processes)
        {
            if (process.CPUMSec > 0.0f && process.ProcessID != 0 && !keep.Contains(process.ProcessID))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///  Finds the name of the busiest process in the trace - the one that owns the most
    ///  CPU samples - so an unscoped read can default to that process's tree rather than
    ///  the whole machine-wide capture.
    /// </summary>
    /// <param name="traceLog">The opened trace whose CPU samples are counted.</param>
    /// <returns>
    ///  The busiest named process's name, or <see langword="null"/> when the trace
    ///  carries no CPU samples attributable to a named process (so no automatic scope
    ///  applies).
    /// </returns>
    /// <remarks>
    ///  <para>
    ///   "Busiest" is ranked by <em>CPU sample count</em>, not <see cref="TraceProcess.CPUMSec"/>.
    ///   The rankings this scope feeds are built from CPU samples, so the process that
    ///   owns the most samples is by definition the one the analysis is about. CPUMSec
    ///   can disagree sharply on a machine-wide capture: a long-lived background service
    ///   (antivirus, a VPN client) accumulates more kernel CPU time across the whole
    ///   capture window than a short benchmark, yet carries a tiny fraction of the
    ///   profile's samples - which made a CPUMSec heuristic auto-scope to the wrong
    ///   process. Counting samples is one extra lightweight event pass (process id only,
    ///   no stack walk); it runs only for the automatic scope (no explicit process
    ///   given) and its result is cached with the loaded trace.
    ///  </para>
    ///  <para>
    ///   The matched name still resolves to the whole tree (the process plus its
    ///   descendants) at scope time, so a host that launches the measured work in a
    ///   child is covered.
    ///  </para>
    /// </remarks>
    public static string? FindBusiestProcessName(TraceLog traceLog)
    {
        // Count CPU samples per process id. The predicate mirrors the CPU-sample
        // selection in TraceLogReader exactly (ETW SampledProfileTraceData, EventPipe
        // ClrThreadSampleTraceData excluding error samples) so the busiest process is
        // chosen by the same events the rankings are built from.
        Dictionary<int, int> samplesByProcess = [];
        foreach (TraceEvent data in traceLog.Events)
        {
            if (data is ClrThreadSampleTraceData clrSample)
            {
                if (clrSample.Type == ClrThreadSampleType.Error)
                {
                    continue;
                }
            }
            else if (data is not SampledProfileTraceData)
            {
                continue;
            }

            int pid = data.ProcessID;
            if (pid > 0)
            {
                samplesByProcess[pid] = samplesByProcess.GetValueOrDefault(pid) + 1;
            }
        }

        if (samplesByProcess.Count == 0)
        {
            return null;
        }

        // The Idle process (pid 0) is bookkeeping, not workload, and an unnamed process
        // cannot be matched by a name substring later - skip both.
        string? busiest = null;
        int maxSamples = 0;
        foreach (TraceProcess process in traceLog.Processes)
        {
            if (process.ProcessID == 0 || string.IsNullOrEmpty(process.Name))
            {
                continue;
            }

            int count = samplesByProcess.GetValueOrDefault(process.ProcessID);
            if (count > maxSamples)
            {
                maxSamples = count;
                busiest = process.Name;
            }
        }

        return busiest;
    }

    /// <summary>
    ///  Resolves a <see cref="ProcessScope"/> to the set of process IDs in the matched
    ///  process tree: every process the scope's selector matches, plus - when the scope
    ///  includes children - all of their descendants, found by walking each process's
    ///  parent chain.
    /// </summary>
    /// <param name="traceLog">The opened trace whose process table is queried.</param>
    /// <param name="scope">The scope to resolve.</param>
    /// <param name="warnings">Collects advisories about how the selector matched, or <see langword="null"/> to discard them.</param>
    /// <returns>The process IDs in the scoped tree; empty when nothing matches.</returns>
    /// <exception cref="ArgumentException">
    ///  A requested process id was reused by more than one process in the trace.
    /// </exception>
    public static HashSet<int> ResolvePids(TraceLog traceLog, ProcessScope scope, List<string>? warnings = null)
    {
        HashSet<int> roots = ResolveRoots(traceLog, scope.Selector, warnings);
        return scope.IncludeChildren ? IncludeDescendants(traceLog, roots) : roots;
    }

    private static HashSet<int> ResolveRoots(
        TraceLog traceLog,
        ProcessSelector selector,
        List<string>? warnings)
    {
        HashSet<int> roots = [];
        switch (selector)
        {
            case ProcessIdSelector idSelector:
                ResolveIdRoots(traceLog, idSelector, roots, warnings);
                break;
            case ProcessNameSelector nameSelector:
                ResolveNameRoots(traceLog, nameSelector, roots, warnings);
                break;
        }

        return roots;
    }

    private static HashSet<int> IncludeDescendants(TraceLog traceLog, HashSet<int> roots)
    {
        HashSet<int> keep = [.. roots];
        foreach (TraceProcess process in traceLog.Processes)
        {
            if (keep.Contains(process.ProcessID))
            {
                continue;
            }

            // A process is in scope when any ancestor is a root. The chain is shallow
            // (host -> job), so walking it per process is cheap.
            for (TraceProcess? ancestor = process.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (roots.Contains(ancestor.ProcessID))
                {
                    keep.Add(process.ProcessID);
                    break;
                }
            }
        }

        return keep;
    }

    private static AppliedProcessScope CreateAppliedScope(
        bool automatic,
        ProcessSelector selector,
        HashSet<int> roots,
        HashSet<int> included,
        bool includeChildren)
    {
        string mode = automatic
            ? "automatic"
            : selector is ProcessIdSelector ? "ids" : "name";
        string? process = selector is ProcessNameSelector nameSelector
            ? nameSelector.NameSubstring
            : null;
        IReadOnlyList<int> requestedIds = selector is ProcessIdSelector idSelector
            ? idSelector.ProcessIds
            : [];
        int[] rootIds = [.. roots.Order()];
        int[] descendantIds = [.. included.Except(roots).Order()];
        return new AppliedProcessScope(
            mode,
            process,
            requestedIds,
            rootIds,
            descendantIds,
            includeChildren);
    }

    private static void ResolveNameRoots(
        TraceLog traceLog,
        ProcessNameSelector selector,
        HashSet<int> roots,
        List<string>? warnings)
    {
        List<TraceProcess> matched = [];
        foreach (TraceProcess process in traceLog.Processes)
        {
            if (process.Name is not null
                && process.Name.IndexOf(selector.NameSubstring, StringComparison.OrdinalIgnoreCase) >= 0
                && roots.Add(process.ProcessID))
            {
                matched.Add(process);
            }
        }

        // A common host name - `dotnet`, `node`, `python` - matches every unrelated
        // instance on a development box, and the resulting ranking blends them with no
        // outward sign. Only independent roots count: a matched parent and its matched
        // child are one tree, and folding them into the count would warn on every
        // ordinary host-launches-worker capture.
        if (warnings is null)
        {
            return;
        }

        int independentRoots = 0;
        foreach (TraceProcess process in matched)
        {
            bool nested = false;
            for (TraceProcess? ancestor = process.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (roots.Contains(ancestor.ProcessID))
                {
                    nested = true;
                    break;
                }
            }

            if (!nested)
            {
                independentRoots++;
            }
        }

        if (independentRoots > 1)
        {
            warnings.Add(
                $"The name '{selector.NameSubstring}' matched {independentRoots} unrelated process trees "
                + $"({FormatIds([.. matched.Select(static process => process.ProcessID).Order()])}); "
                + "they are ranked together. Pass --pid to scope to exact processes.");
        }
    }

    private static void ResolveIdRoots(
        TraceLog traceLog,
        ProcessIdSelector selector,
        HashSet<int> roots,
        List<string>? warnings)
    {
        HashSet<int> requested = [.. selector.ProcessIds];
        Dictionary<int, List<TraceProcess>> matches = [];
        foreach (TraceProcess process in traceLog.Processes)
        {
            if (!requested.Contains(process.ProcessID))
            {
                continue;
            }

            if (!matches.TryGetValue(process.ProcessID, out List<TraceProcess>? sameId))
            {
                sameId = [];
                matches[process.ProcessID] = sameId;
            }

            sameId.Add(process);
        }

        List<int> unmatched = [];
        foreach (int processId in selector.ProcessIds)
        {
            if (!matches.TryGetValue(processId, out List<TraceProcess>? sameId))
            {
                unmatched.Add(processId);
                continue;
            }

            // Windows reuses process ids, and a long capture can hold two unrelated
            // processes under one id. Silently unioning them would attribute a stranger's
            // samples to the workload under the exact selector chosen to prevent that,
            // so refuse and hand back what is needed to disambiguate. No parameter name:
            // the ids are well formed, it is this trace that cannot resolve them, and the
            // CLI and MCP heads surface this message verbatim.
            if (sameId.Count > 1)
            {
                throw new ArgumentException(
                    $"Process id {processId} was reused in this trace by {sameId.Count} processes "
                    + $"({string.Join("; ", sameId.Select(static process =>
                        $"'{process.Name}' started at {process.StartTimeRelativeMsec.ToString("F3", CultureInfo.InvariantCulture)} ms"))}). "
                    + "Scope to a time window that contains only one of them, or select by process name.");
            }

            roots.Add(processId);
        }

        // A manifest replayed against the wrong capture is the common cause here, and a
        // partial match would otherwise look like an ordinary thin result.
        if (warnings is not null && unmatched.Count > 0)
        {
            warnings.Add(
                $"{FormatIds(unmatched)} {(unmatched.Count == 1 ? "was" : "were")} not found in this trace and "
                + "contributed nothing to the scope.");
        }
    }
}
