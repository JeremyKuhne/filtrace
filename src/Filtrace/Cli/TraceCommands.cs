// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.ComponentModel.DataAnnotations;
using ConsoleAppFramework;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  The command surface ConsoleAppFramework binds the verbs to: each public method
///  is a verb, its parameters are the options, and its XML doc comments supply the
///  generated help. The bodies validate the provider selector and delegate the work
///  to <see cref="RankingExecutor"/>.
/// </summary>
internal sealed class TraceCommands
{
    /// <summary>
    ///  Report a trace's identity and quality signals - the orientation step to run first.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="process">Scope a multi-process .etl to the tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Reports the format, sample count, total weight, frame-name resolution,
    ///  sampled source/PDB quality, the busiest threads, available analyses, and
    ///  quality warnings. It is the CLI counterpart of the <c>trace_info</c> tool -
    ///  run it first. Frame names normally come from CLR rundown; source lines require
    ///  exact matching PDBs in <c>--symbols</c>. Like that tool it
    ///  takes an optional <c>--process</c> selector (no
    ///  <c>--all-processes</c> opt-out); use the <c>processes</c> verb to see every
    ///  process in a machine-wide capture.
    /// </remarks>
    [Command("info")]
    public int Info(
        [Argument] string trace,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        string process = "")
    {
        // Mirror the trace_info tool's scope resolution: an explicit name scopes to that
        // process tree, and an empty selector auto-scopes a multi-process capture to the
        // busiest. There is no all-processes opt-out here (run `processes` to list them).
        ScopeRequest? scope = string.IsNullOrEmpty(process) ? null : ScopeRequest.ForProcess(process);
        InfoRequest request = new(trace, symbols, format, scope);
        return InfoExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Rank the hottest frames in a trace by self or inclusive metric weight.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="metric">
    ///  Provider metric to rank: cpu (default), alloc, exceptions, threadtime,
    ///  contention, wait, or activity. The cpu metric weights each sample as 1 ms, so its weights
    ///  are approximate; the relative percentages are exact.
    /// </param>
    /// <param name="measure">-m, Which measure to report: self (leaf time, helpers folded) or inclusive.</param>
    /// <param name="root">Substring scoping the ranking to the subtree under a frame.</param>
    /// <param name="top">-n, Maximum number of rows to return.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when symbol resolution is below the trusted threshold.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <param name="activity">Scope the ranking to one start-stop activity by task name - the CPU samples taken inside that request/job (cpu metric only); omit for the whole trace.</param>
    /// <param name="time">Scope to a time window 'start,end' in ms relative to the trace start; either bound may be omitted (e.g. 1000,5000 or 1000, or ,5000). Applies to every metric on .nettrace/.etl; speedscope warns and ignores it.</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <param name="nativeSymbols">Resolve native runtime frames (GC, JIT, memset/memcpy) from the Microsoft public symbol server; opt-in, fetches over the network. .etl CPU captures only.</param>
    /// <param name="symbolCache">Local cache directory for downloaded native PDBs; omit for the default under the temp path.</param>
    /// <param name="noFold">Fold only the synthetic sample markers, not the JIT-helper thunks, so native runtime leaves rank on their own. Mutually exclusive with --fold.</param>
    /// <returns>A process exit code.</returns>
    [Command("rank")]
    public int Rank(
        [Argument] string trace,
        string metric = RankRequestFactory.CpuMetric,
        Measure measure = Measure.Self,
        string root = "",
        [Range(1, int.MaxValue)] int top = RankRequestFactory.DefaultTop,
        string[]? fold = null,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false,
        string activity = "",
        string time = "",
        bool benchmark = false,
        bool nativeSymbols = false,
        string symbolCache = "",
        bool noFold = false)
    {
        if (!RankRequestFactory.TryResolveMetric(metric, out TraceMetric resolved))
        {
            Console.Error.WriteLine(
                $"Unknown metric '{metric}'. Supported stack metrics: {string.Join(", ", TraceMetricSelector.Selectors)}.");
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        // The activity scope filters CPU samples by the request/job they were taken in,
        // so it applies to the cpu metric only; the other metrics' providers do not read
        // it. Reject the combination rather than silently ignore it.
        if (!string.IsNullOrEmpty(activity) && resolved != TraceMetric.Cpu)
        {
            Console.Error.WriteLine(
                "The --activity scope applies to the cpu metric only. Use --metric cpu (or omit --metric) to scope to an activity.");
            return ExitCodes.UsageError;
        }

        scope = scope.WithActivity(activity);

        if (!TimeWindow.TryParse(time, out double? startMSec, out double? endMSec, out string? timeError))
        {
            Console.Error.WriteLine(timeError);
            return ExitCodes.UsageError;
        }

        scope = scope.WithTimeWindow(startMSec, endMSec);

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveFold(fold, noFold, out string[]? foldPatterns, out string? foldError))
        {
            Console.Error.WriteLine(foldError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveSymbolOptions(nativeSymbols, symbolCache, out SymbolOptions symbolOptions, out string? symbolCacheError))
        {
            Console.Error.WriteLine(symbolCacheError);
            return ExitCodes.UsageError;
        }

        RankRequest request = RankRequestFactory.Create(
            trace, resolved, measure, resolvedRoot, top, foldPatterns, symbols, format, strict, scope, symbolOptions);
        return RankingExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Rank CPU-time hotspots; the shortcut for 'rank --metric cpu'.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="measure">
    ///  -m, Which measure to report: self (leaf time, helpers folded) or inclusive. Each
    ///  sample weighs 1 ms, so the weights are approximate; the relative percentages are
    ///  exact.
    /// </param>
    /// <param name="root">Substring scoping the ranking to the subtree under a frame.</param>
    /// <param name="top">-n, Maximum number of rows to return.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when symbol resolution is below the trusted threshold.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <param name="nativeSymbols">Resolve native runtime frames (GC, JIT, memset/memcpy) from the Microsoft public symbol server; opt-in, fetches over the network. .etl CPU captures only.</param>
    /// <param name="symbolCache">Local cache directory for downloaded native PDBs; omit for the default under the temp path.</param>
    /// <param name="noFold">Fold only the synthetic sample markers, not the JIT-helper thunks, so native runtime leaves rank on their own. Mutually exclusive with --fold.</param>
    /// <returns>A process exit code.</returns>
    [Command("cpu")]
    public int Cpu(
        [Argument] string trace,
        Measure measure = Measure.Self,
        string root = "",
        [Range(1, int.MaxValue)] int top = RankRequestFactory.DefaultTop,
        string[]? fold = null,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false,
        bool benchmark = false,
        bool nativeSymbols = false,
        string symbolCache = "",
        bool noFold = false)
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveFold(fold, noFold, out string[]? foldPatterns, out string? foldError))
        {
            Console.Error.WriteLine(foldError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveSymbolOptions(nativeSymbols, symbolCache, out SymbolOptions symbolOptions, out string? symbolCacheError))
        {
            Console.Error.WriteLine(symbolCacheError);
            return ExitCodes.UsageError;
        }

        RankRequest request = RankRequestFactory.Create(
            trace, TraceMetric.Cpu, measure, resolvedRoot, top, foldPatterns, symbols, format, strict, scope, symbolOptions);
        return RankingExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Rank allocation hotspots by bytes; the shortcut for 'rank --metric alloc'.
    /// </summary>
    /// <param name="trace">Path to a .nettrace EventPipe file captured with allocation sampling.</param>
    /// <param name="measure">-m, Which measure to report: self (the allocating site) or inclusive (its subtree).</param>
    /// <param name="root">Substring scoping the ranking to the subtree under a frame.</param>
    /// <param name="top">-n, Maximum number of rows to return.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Allocation frames resolve from the trace's own CLR rundown, so this verb has
    ///  no <c>--symbols</c> or <c>--strict</c> option: those resolve and gate native
    ///  frames, which the allocation view does not depend on. It also has no
    ///  <c>--process</c> / <c>--all-processes</c> option: an EventPipe <c>.nettrace</c>
    ///  is single-process, so there is nothing to scope across.
    /// </remarks>
    [Command("alloc")]
    public int Alloc(
        [Argument] string trace,
        Measure measure = Measure.Self,
        string root = "",
        [Range(1, int.MaxValue)] int top = RankRequestFactory.DefaultTop,
        string[]? fold = null,
        OutputFormat format = OutputFormat.Text,
        bool benchmark = false)
    {
        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        RankRequest request = RankRequestFactory.Create(
            trace, TraceMetric.Allocations, measure, resolvedRoot, top, fold, symbols: null, format, strict: false);
        return RankingExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Rank exceptions by type (self) or throw path (inclusive) by count; the shortcut for 'rank --metric exceptions'.
    /// </summary>
    /// <param name="trace">Path to a .nettrace EventPipe file carrying exception-throw events.</param>
    /// <param name="measure">-m, Which measure to report: self (the exception type) or inclusive (the throw sites and their callers).</param>
    /// <param name="root">Substring scoping the ranking to the subtree under a frame.</param>
    /// <param name="top">-n, Maximum number of rows to return.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Throw-site frames resolve from the trace's own CLR rundown, so this verb has
    ///  no <c>--symbols</c> or <c>--strict</c> option: those resolve and gate native
    ///  frames, which the exception view does not depend on. It also has no
    ///  <c>--process</c> / <c>--all-processes</c> option: an EventPipe <c>.nettrace</c>
    ///  is single-process, so there is nothing to scope across.
    /// </remarks>
    [Command("exceptions")]
    public int Exceptions(
        [Argument] string trace,
        Measure measure = Measure.Self,
        string root = "",
        [Range(1, int.MaxValue)] int top = RankRequestFactory.DefaultTop,
        string[]? fold = null,
        OutputFormat format = OutputFormat.Text,
        bool benchmark = false)
    {
        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        RankRequest request = RankRequestFactory.Create(
            trace, TraceMetric.Exceptions, measure, resolvedRoot, top, fold, symbols: null, format, strict: false);
        return RankingExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Rank where wall-clock time went - running and blocked - by elapsed
    ///  milliseconds; the shortcut for 'rank --metric threadtime'.
    /// </summary>
    /// <param name="trace">Path to an .etl ETW capture taken with the context-switch keywords.</param>
    /// <param name="measure">-m, Which measure to report: self (the leaf state) or inclusive (its subtree).</param>
    /// <param name="root">Substring scoping the ranking to the subtree under a frame.</param>
    /// <param name="top">-n, Maximum number of rows to return.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest.</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Unlike CPU sampling, thread time accounts for off-CPU (blocked) intervals, so
    ///  a stack's weight is elapsed time rather than busy time. Reading an <c>.etl</c>
    ///  requires the ETW conversion, which is available on Windows only. Frames
    ///  resolve from the capture itself, so this verb has no <c>--symbols</c> or
    ///  <c>--strict</c> option.
    /// </remarks>
    [Command("threadtime")]
    public int ThreadTime(
        [Argument] string trace,
        Measure measure = Measure.Self,
        string root = "",
        [Range(1, int.MaxValue)] int top = RankRequestFactory.DefaultTop,
        string[]? fold = null,
        OutputFormat format = OutputFormat.Text,
        string process = "",
        bool allProcesses = false,
        bool benchmark = false)
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        RankRequest request = RankRequestFactory.Create(
            trace, TraceMetric.ThreadTime, measure, resolvedRoot, top, fold, symbols: null, format, strict: false, scope);
        return RankingExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Report the immediate CPU callers of a frame; the drill-down after a CPU ranking.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="frame">Substring of the focus frame whose callers are reported.</param>
    /// <param name="root">Substring scoping the analysis to the subtree under a frame.</param>
    /// <param name="top">-n, Maximum number of caller rows to return.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when symbol resolution is below the trusted threshold.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <param name="callees">Also report the focus frame's immediate callees (a caller/callee view).</param>
    /// <returns>A process exit code.</returns>
    [Command("callers")]
    public int Callers(
        [Argument] string trace,
        [Argument] string frame,
        string root = "",
        [Range(1, int.MaxValue)] int top = RankRequestFactory.DefaultTop,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false,
        bool benchmark = false,
        bool callees = false)
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        CallersRequest request = new(trace, frame, resolvedRoot, top, symbols, format, strict, scope, callees);
        return CallersExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Rank the hottest CPU source lines of the scoped methods.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="method">Substring scoping the ranking to matching methods; omit for every method.</param>
    /// <param name="top">-n, Maximum number of rows to return.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when symbol resolution is below the trusted threshold.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <returns>A process exit code.</returns>
    [Command("lines")]
    public int Lines(
        [Argument] string trace,
        string method = "",
        [Range(1, int.MaxValue)] int top = RankRequestFactory.DefaultTop,
        string[]? fold = null,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false)
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        IReadOnlyList<string> foldPatterns = fold is { Length: > 0 } ? fold : FrameNames.DefaultFoldPatterns;
        LinesRequest request = new(trace, method, foldPatterns, top, symbols, format, strict, scope);
        return LinesExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Build a per-line CPU heat map for a source file.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="file">Source file to map; a full on-disk path also overlays the heat onto the source.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when symbol resolution is below the trusted threshold.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <returns>A process exit code.</returns>
    [Command("heatmap")]
    public int Heatmap(
        [Argument] string trace,
        [Argument] string file,
        string[]? fold = null,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false)
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        IReadOnlyList<string> foldPatterns = fold is { Length: > 0 } ? fold : FrameNames.DefaultFoldPatterns;
        HeatmapRequest request = new(trace, file, foldPatterns, symbols, format, strict, scope);
        return HeatmapExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  List the processes in a trace, ranked by CPU-sample weight, so a multi-process
    ///  capture can be scoped to the right one with the --process option on the other
    ///  verbs.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <returns>A process exit code.</returns>
    [Command("processes")]
    public int Processes(
        [Argument] string trace,
        OutputFormat format = OutputFormat.Text)
    {
        ProcessesRequest request = new(trace, format);
        return ProcessesExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Summarize CPU self-time by runtime work category - zeroing, copying, GC,
    ///  write-barrier, JIT, or other - answering "where did the time go: zeroing memory?
    ///  copying strings? in the GC?". Pair with --native-symbols so the native runtime
    ///  work resolves; without it the native leaves fall in 'other'.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="root">Substring scoping the classification to the subtree under a frame.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when symbol resolution is below the trusted threshold.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <param name="nativeSymbols">Resolve native runtime frames (GC, JIT, memset/memcpy) from the Microsoft public symbol server; opt-in, fetches over the network. .etl captures only.</param>
    /// <param name="symbolCache">Local cache directory for downloaded native PDBs; omit for the default under the temp path.</param>
    /// <returns>A process exit code.</returns>
    [Command("classify")]
    public int Classify(
        [Argument] string trace,
        string root = "",
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false,
        bool benchmark = false,
        bool nativeSymbols = false,
        string symbolCache = "")
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveSymbolOptions(nativeSymbols, symbolCache, out SymbolOptions symbolOptions, out string? symbolCacheError))
        {
            Console.Error.WriteLine(symbolCacheError);
            return ExitCodes.UsageError;
        }

        ClassifyRequest request = new(trace, resolvedRoot, symbols, format, strict, scope, symbolOptions);
        return ClassifyExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Report physical disk I/O by file: bytes read and written to each file, and disk service time.
    /// </summary>
    /// <param name="trace">Path to a Windows ETW .etl file captured with the DiskIO (and DiskFileIO, for file names) kernel keyword.</param>
    /// <param name="top">-n, Maximum number of per-file rows to show, ranked by disk time.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  A structured report, not a stack ranking. Physical disk events are recorded after
    ///  the file-system cache, so they show the real disk pressure the logical file APIs
    ///  hide. Windows ETW only; .nettrace and speedscope inputs are rejected.
    /// </remarks>
    [Command("diskio")]
    public int DiskIo(
        [Argument] string trace,
        [Range(1, int.MaxValue)] int top = 25,
        OutputFormat format = OutputFormat.Text)
    {
        DiskIoRequest request = new(trace, top, format);
        return DiskIoExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Compare two like-for-like CPU traces or capture manifests by absolute and normalized sampled-time change.
    /// </summary>
    /// <param name="before">Baseline trace or capture manifest.json.</param>
    /// <param name="after">Current trace or capture manifest.json; both inputs must have the same kind.</param>
    /// <param name="measure">-m, Which measure to compare: self (leaf time, helpers folded) or inclusive.</param>
    /// <param name="root">Substring scoping both rankings to the subtree under a frame.</param>
    /// <param name="top">-n, Maximum number of changed rows to return.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when either trace's symbol resolution is below the trusted threshold.</param>
    /// <param name="process">Scope both traces to the process tree whose name contains this; omit to auto-scope.</param>
    /// <param name="allProcesses">Read every process in both traces instead of auto-scoping.</param>
    /// <param name="benchmark">Scope both traces to the BenchmarkDotNet measured-workload subtree.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Manifest diffs pair at most 24 cases by benchmark plus parameters and show at
    ///  most 5 changed rows per case. Only manifests can supply per-operation values.
    /// </remarks>
    [Command("diff")]
    public int Diff(
        [Argument] string before,
        [Argument] string after,
        Measure measure = Measure.Self,
        string root = "",
        [Range(1, int.MaxValue)] int top = RankRequestFactory.DefaultTop,
        string[]? fold = null,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false,
        bool benchmark = false)
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        IReadOnlyList<string> foldPatterns = fold is { Length: > 0 } ? fold : FrameNames.DefaultFoldPatterns;
        DiffRequest request = new(
            before,
            after,
            resolvedRoot,
            top,
            foldPatterns,
            measure,
            format,
            symbols,
            strict,
            scope);
        return DiffExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>Run one compact ranking query across up to 24 cases in a capture manifest.</summary>
    /// <param name="manifest">Path to a capture helper manifest.json.</param>
    /// <param name="metric">Provider metric: cpu, threadtime, alloc, exceptions, contention, wait, or activity.</param>
    /// <param name="measure">-m, self or inclusive.</param>
    /// <param name="root">Optional root frame applied to every case.</param>
    /// <param name="fold">Extra leaf-frame fold regexes; omit for defaults.</param>
    /// <param name="symbols">Optional symbol directory overriding each case's recorded directory.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when any loaded case has poor symbol resolution.</param>
    /// <param name="process">Process substring overriding the manifest process.</param>
    /// <param name="allProcesses">Read every process rather than manifest/automatic scope.</param>
    /// <param name="benchmark">Use the BenchmarkDotNet measured-workload root.</param>
    /// <returns>A process exit code.</returns>
    [Command("batch")]
    public int Batch(
        [Argument] string manifest,
        string metric = RankRequestFactory.CpuMetric,
        Measure measure = Measure.Self,
        string root = "",
        string[]? fold = null,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false,
        bool benchmark = false)
    {
        if (!RankRequestFactory.TryResolveMetric(metric, out TraceMetric resolvedMetric))
        {
            Console.Error.WriteLine(
                $"Unknown metric '{metric}'. Valid metrics: {string.Join(", ", TraceMetricSelector.Selectors)}.");
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        IReadOnlyList<string> foldPatterns = fold is { Length: > 0 }
            ? fold
            : FrameNames.DefaultFoldPatterns;
        BatchRequest request = new(
            manifest,
            resolvedMetric,
            resolvedRoot,
            foldPatterns,
            measure,
            format,
            symbols,
            strict,
            scope);
        return BatchExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Show the top-down CPU call tree, following the hot path from the root into its callees.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="root">Substring scoping the tree to the subtree under a frame.</param>
    /// <param name="maxDepth">-d, Maximum number of frame levels to expand below the root.</param>
    /// <param name="minPct">Minimum share of the scoped total (percent) a node must have to appear; 0 shows all.</param>
    /// <param name="fold">Extra leaf-frame fold regexes (comma-separated); omit to use the built-in defaults.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <param name="strict">Exit 3 when symbol resolution is below the trusted threshold.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <returns>A process exit code.</returns>
    [Command("tree")]
    public int Tree(
        [Argument] string trace,
        string root = "",
        [Range(0, FoldingAggregator.MaxTreeDepth)] int maxDepth = TreeRequest.DefaultMaxDepth,
        [Range(0.0, 100.0)] double minPct = TreeRequest.DefaultMinPercent,
        string[]? fold = null,
        string? symbols = null,
        OutputFormat format = OutputFormat.Text,
        bool strict = false,
        string process = "",
        bool allProcesses = false,
        bool benchmark = false)
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        IReadOnlyList<string> foldPatterns = fold is { Length: > 0 } ? fold : FrameNames.DefaultFoldPatterns;
        TreeRequest request = new(trace, resolvedRoot, foldPatterns, maxDepth, minPct, symbols, format, strict, scope);
        return TreeExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Export a trace to a flame-graph file for speedscope or chrome://tracing.
    /// </summary>
    /// <param name="trace">Path to a .speedscope.json, .nettrace, or .etl file.</param>
    /// <param name="format">Flame-graph format: speedscope or chromium.</param>
    /// <param name="output">-o, Output file path; omit to write to standard output.</param>
    /// <param name="symbols">-s, Build-output directory whose PDBs map managed code to source lines.</param>
    /// <param name="name">Profile name shown in the viewer.</param>
    /// <param name="process">Scope to the process tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <param name="root">Substring scoping the export to the subtree under a frame.</param>
    /// <param name="benchmark">Scope to the BenchmarkDotNet measured-workload subtree (preset root); for BDN captures.</param>
    /// <param name="nativeSymbols">Resolve native runtime frames (GC, JIT, memset/memcpy) from the Microsoft public symbol server; opt-in, fetches over the network. .etl captures only.</param>
    /// <param name="symbolCache">Local cache directory for downloaded native PDBs; omit for the default under the temp path.</param>
    /// <returns>A process exit code.</returns>
    [Command("export")]
    public int Export(
        [Argument] string trace,
        ExportFormat format = ExportFormat.Speedscope,
        string? output = null,
        string? symbols = null,
        string name = "filtrace",
        string process = "",
        bool allProcesses = false,
        string root = "",
        bool benchmark = false,
        bool nativeSymbols = false,
        string symbolCache = "")
    {
        if (!RankRequestFactory.TryResolveScope(process, allProcesses, out ScopeRequest scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveRoot(root, benchmark, out string resolvedRoot, out string? rootError))
        {
            Console.Error.WriteLine(rootError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveSymbolOptions(nativeSymbols, symbolCache, out SymbolOptions symbolOptions, out string? symbolCacheError))
        {
            Console.Error.WriteLine(symbolCacheError);
            return ExitCodes.UsageError;
        }

        ExportRequest request = new(trace, format, output, symbols, name, scope, resolvedRoot, symbolOptions);
        return ExportExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Report garbage-collection behavior: counts by generation, pause-time summary, and per-collection detail.
    /// </summary>
    /// <param name="trace">Path to a .nettrace EventPipe file captured with GC events (GcVerbose).</param>
    /// <param name="top">-n, Maximum number of per-collection rows to show, ranked by pause time.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  This is a structured report, not a stack ranking: the summary always reflects
    ///  every collection, while the detail rows are capped and ranked by pause time.
    /// </remarks>
    [Command("gcstats")]
    public int GcStats(
        [Argument] string trace,
        [Range(1, int.MaxValue)] int top = 50,
        OutputFormat format = OutputFormat.Text)
    {
        GcStatsRequest request = new(trace, top, format);
        return GcStatsExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Correlate what a trace was doing over time: per-bucket GC, CPU, exception, allocation, and JIT activity.
    /// </summary>
    /// <param name="trace">Path to a .nettrace EventPipe or .etl ETW file (a speedscope export is rejected).</param>
    /// <param name="lanes">Comma-separated lanes to include: gc, cpu, exceptions, alloc, jit; omit for every lane.</param>
    /// <param name="buckets">-n, Number of equal time buckets to divide the window into (clamped to 5-200).</param>
    /// <param name="time">Scope to a time window 'start,end' in ms relative to the trace start; either bound may be omitted (e.g. 1000,5000 or 1000, or ,5000).</param>
    /// <param name="process">Scope a multi-process .etl to the tree whose name contains this; omit to auto-scope to the busiest.</param>
    /// <param name="allProcesses">Read every process instead of auto-scoping to the busiest (multi-process captures).</param>
    /// <param name="format">Render format: text or json.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  An orientation view, not a stack ranking: it shows when activity happened - the
    ///  busy window - so a ranking can then be scoped to it with --time. The GC lane
    ///  comes from the runtime's per-collection records; the CPU, exception, allocation,
    ///  and JIT lanes are counted from the event stream both EventPipe and ETW carry.
    /// </remarks>
    [Command("timeline")]
    public int Timeline(
        [Argument] string trace,
        string lanes = "",
        int buckets = TimelineProvider.DefaultBucketCount,
        string time = "",
        string process = "",
        bool allProcesses = false,
        OutputFormat format = OutputFormat.Text)
    {
        TimelineRequest request = new(trace, time, lanes, buckets, process, allProcesses, format);
        return TimelineExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Report just-in-time compilation: method count, compile-time summary, and per-method detail.
    /// </summary>
    /// <param name="trace">Path to a .nettrace EventPipe file captured with JIT events.</param>
    /// <param name="top">-n, Maximum number of per-method rows to show, ranked by compile time.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  This is a structured report, not a stack ranking: the summary always reflects
    ///  every method, while the detail rows are capped and ranked by compile time.
    /// </remarks>
    [Command("jitstats")]
    public int JitStats(
        [Argument] string trace,
        [Range(1, int.MaxValue)] int top = 25,
        OutputFormat format = OutputFormat.Text)
    {
        JitStatsRequest request = new(trace, top, format);
        return JitStatsExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Report thread-pool worker-thread adjustments: how often the pool grew, and how
    ///  often because it detected starvation (the classic sync-over-async hang signal).
    /// </summary>
    /// <param name="trace">Path to a .nettrace EventPipe file.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  This is a structured report, not a stack ranking: a run of Starvation adjustments
    ///  means the pool kept injecting threads because queued work was not completing.
    /// </remarks>
    [Command("threadpool")]
    public int ThreadPool(
        [Argument] string trace,
        OutputFormat format = OutputFormat.Text)
    {
        ThreadPoolRequest request = new(trace, format);
        return ThreadPoolExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Query the trace's raw events by name, with paging and a per-event payload cap.
    /// </summary>
    /// <param name="trace">Path to a .nettrace EventPipe or Windows ETW .etl file.</param>
    /// <param name="name">Case-insensitive substring matched against provider/event; omit to match every event.</param>
    /// <param name="skip">Number of matches to skip, for paging.</param>
    /// <param name="take">-n, Maximum number of matches to return on this page.</param>
    /// <param name="maxPayload">Per-event payload character cap; 0 omits payloads.</param>
    /// <param name="payload">Keep only events whose payload values contain this (case-insensitive); omit for no payload filter.</param>
    /// <param name="pid">Keep only events from this process id; omit for every process.</param>
    /// <param name="tid">Keep only events on this thread id; omit for every thread.</param>
    /// <param name="format">Render format: text or json.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Paging (<c>--skip</c> / <c>--take</c>) and the payload cap keep the output
    ///  bounded even when a query matches hundreds of thousands of events.
    /// </remarks>
    [Command("events")]
    public int Events(
        [Argument] string trace,
        string name = "",
        [Range(0, int.MaxValue)] int skip = 0,
        [Range(1, int.MaxValue)] int take = 50,
        [Range(0, int.MaxValue)] int maxPayload = EventQueryProvider.DefaultMaxPayloadChars,
        string payload = "",
        [Range(-1, int.MaxValue)] int pid = -1,
        [Range(-1, int.MaxValue)] int tid = -1,
        OutputFormat format = OutputFormat.Text)
    {
        EventsRequest request = new(
            trace, name, skip, take, maxPayload, payload,
            pid >= 0 ? pid : null, tid >= 0 ? tid : null, format);
        return EventsExecutor.Run(request, Console.Out, Console.Error);
    }

    /// <summary>
    ///  Build the ETLX conversion cache up front so the first analysis query is fast.
    /// </summary>
    /// <param name="trace">Path to a .nettrace or .etl file (a speedscope export has no cache).</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Every analysis of a .nettrace or .etl first converts it to an indexed ETLX file
    ///  beside the source; TraceEvent reuses that cache on later reads. Converting ahead
    ///  of time moves that one-time cost out of the first real query.
    /// </remarks>
    [Command("convert")]
    public int Convert([Argument] string trace) =>
        FileOpsExecutor.Convert(trace, Console.Out, Console.Error);

    /// <summary>
    ///  Remove the ETLX conversion cache beside a trace to force a rebuild on next read.
    /// </summary>
    /// <param name="trace">Path to a .nettrace or .etl file whose ETLX cache to remove.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Use this when a cache is suspected stale (for example after the source trace was
    ///  replaced); the next analysis rebuilds it. A missing cache is reported, not an error.
    /// </remarks>
    [Command("clean")]
    public int Clean([Argument] string trace) =>
        FileOpsExecutor.Clean(trace, Console.Out, Console.Error);

    /// <summary>
    ///  Record a Windows ETW (.etl) trace of a launched executable, then print the analysis
    ///  commands the capture unlocks. Windows-only and requires Administrator; for an
    ///  EventPipe (.nettrace) capture use dotnet-trace (cross-platform).
    /// </summary>
    /// <param name="launch">Path to the executable to launch and trace (the built app, never 'dotnet run').</param>
    /// <param name="output">Path of the .etl file to write.</param>
    /// <param name="metric">What to tune the capture for: cpu (default) or threadtime (adds context switches for wall-clock time).</param>
    /// <param name="launchArgs">Arguments passed to the launched executable, as one command-line string.</param>
    /// <param name="cpuMs">CPU sample interval in milliseconds.</param>
    /// <param name="duration">Optional cap on capture length in seconds; 0 (default) captures until the process exits.</param>
    /// <param name="maxSizeMb">Optional cap on the capture's on-disk size in megabytes; 0 (default) writes an unbounded file. When set, a circular buffer keeps the last N MB - size it to hold the run, since a full ring overwrites the oldest events and can drop early JIT method names.</param>
    /// <returns>A process exit code.</returns>
    /// <remarks>
    ///  Reproduces a PerfView-style capture with TraceEvent's session API, so no external
    ///  recorder is needed. A launch capture needs no CLR rundown; managed frames resolve
    ///  from the live JIT events. The written .etl is machine-wide, so the printed commands
    ///  scope to the launched process with --process.
    /// </remarks>
    [Command("collect")]
    public int Collect(
        string launch,
        string output,
        string metric = "cpu",
        string launchArgs = "",
        [Range(1, 1000)] double cpuMs = 1.0,
        [Range(0, int.MaxValue)] int duration = 0,
        [Range(0, int.MaxValue)] int maxSizeMb = 0)
    {
        if (!TryResolveCollectMetric(metric, out CollectMetric resolved))
        {
            Console.Error.WriteLine($"Unknown metric '{metric}'. Supported capture metrics: cpu, threadtime.");
            return ExitCodes.UsageError;
        }

        EtwCollectRequest request = new()
        {
            LaunchExecutable = launch,
            LaunchArguments = launchArgs,
            Metric = resolved,
            CpuSampleMSec = cpuMs,
            DurationSeconds = duration > 0 ? duration : null,
            MaxSizeMB = maxSizeMb > 0 ? maxSizeMb : null,
            OutputPath = output,
        };

        return CollectExecutor.Run(request, Console.Out, Console.Error);
    }

    // Resolve the collect --metric selector to its capture keyword set.
    private static bool TryResolveCollectMetric(string metric, out CollectMetric result)
    {
        switch (metric?.Trim().ToLowerInvariant())
        {
            case "cpu":
                result = CollectMetric.Cpu;
                return true;
            case "threadtime":
                result = CollectMetric.ThreadTime;
                return true;
            default:
                result = CollectMetric.Cpu;
                return false;
        }
    }
}
