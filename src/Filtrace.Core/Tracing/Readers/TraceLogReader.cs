// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Shared core for the TraceEvent-backed readers. Converts an ETW (<c>.etl</c>)
///  or EventPipe (<c>.nettrace</c>) trace into the normalized weighted-sample
///  model by walking the call stack of every sampled-profile event.
/// </summary>
/// <remarks>
///  <para>
///   Both formats normalize, through <see cref="TraceLog"/>, onto the same
///   <see cref="SampledProfileTraceData"/> CPU-sample event with a resolvable
///   <see cref="TraceCallStack"/>. Managed method names resolve from the CLR
///   rundown embedded in the trace, so no external symbol server is needed for
///   managed frames; native frames may remain unresolved.
///  </para>
///  <para>
///   Each sample is weighted as one millisecond. Sampled-profile CPU profiling
///   runs at roughly a 1 kHz cadence on both runtimes, so scoped durations are
///   accurate to within the sampling interval and the relative percentages -
///   what the rankings actually report - are unaffected.
///  </para>
/// </remarks>
internal abstract class TraceLogReader : ITraceReader
{
    /// <inheritdoc/>
    public abstract TraceFormat Format { get; }

    /// <inheritdoc/>
    public abstract bool CanRead(string path);

    /// <summary>
    ///  Converts the trace at <paramref name="path"/> to an ETLX
    ///  <see cref="TraceLog"/> the caller then reads.
    /// </summary>
    /// <param name="path">The trace file path.</param>
    /// <param name="cacheState">How the ETLX cache request was satisfied.</param>
    /// <returns>The opened trace log.</returns>
    protected abstract TraceLog OpenTraceLog(string path, out EtlxCacheState cacheState);

    /// <inheritdoc/>
    public TraceReadResult Read(
        string path,
        string? symbolsDirectory = null,
        ScopeRequest? scope = null,
        SymbolOptions? symbolOptions = null)
    {
        symbolsDirectory = NormalizeSymbolsDirectory(symbolsDirectory);

        EtlxCacheState cacheState;
        using TraceLog traceLog = OpenTraceLog(path, out cacheState);

        // Local-only symbol reader: an empty symbol path never reaches a symbol
        // server, but portable PDBs sitting next to a traced module still
        // resolve, which is all the managed touki frames need for line-level
        // attribution. Frames without a local PDB (BCL, OS) simply carry no line.
        using SymbolReader symbolReader = new(TextWriter.Null, "", null);

        // touki and its sibling assemblies ship embedded portable PDBs, which
        // TraceEvent's SymbolReader cannot read, and BenchmarkDotNet's ephemeral
        // run directory is gone by analysis time. When the caller points us at a
        // build-output directory we re-materialize those embedded PDBs as
        // standalone files in a temp directory and add it to the symbol path so
        // TraceEvent can match a module by its PDB GUID and resolve source lines.
        string? extractedPdbDirectory = symbolsDirectory is null
            ? null
            : EmbeddedPdbExtractor.Extract(symbolsDirectory);
        string? localSymbolPath = null;

        // The name the scope resolved to (set by ResolveScope below), surfaced as a
        // warning so the caller knows a machine-wide capture was narrowed automatically.
        string? appliedScopeName = null;

        try
        {
            if (extractedPdbDirectory is not null)
            {
                SymbolPath symbolPath = new(symbolsDirectory);
                symbolPath.Add(extractedPdbDirectory);
                localSymbolPath = symbolPath.ToString();
                symbolReader.SymbolPath = localSymbolPath;
            }
            else if (!string.IsNullOrEmpty(symbolsDirectory))
            {
                localSymbolPath = symbolsDirectory;
                symbolReader.SymbolPath = localSymbolPath;
            }

            // Opt-in native runtime symbols: point the reader at the Microsoft public
            // symbol server (a local cache fronting it) and resolve the unmanaged
            // runtime modules - the GC, the JIT, memset/memcpy, write barriers - whose
            // PDBs are not in the trace. Off by default so the common path stays offline
            // and deterministic; managed frames resolve from the rundown regardless.
            if (symbolOptions is { ResolveNativeRuntime: true })
            {
                ResolveNativeRuntimeSymbols(traceLog, symbolReader, symbolOptions);
            }

            // Resolve the scope intent (an explicit name, the busiest process under the
            // automatic default, or every process when opted out) to the set of process
            // IDs to keep. A null request means "unspecified", which is the automatic
            // default - the same as ScopeRequest.Auto - so a caller that passes nothing
            // still gets scenario scope. A null pid set means no scoping (every process,
            // the all-processes opt-out). This is lossless: the trace is fully
            // symbol-resolved by TraceLog before any sample is dropped.
            HashSet<int>? scopePids = ProcessTree.ResolveScope(
                traceLog, scope ?? ScopeRequest.Auto, out appliedScopeName);

            // When an activity scope is requested, pre-pass the trace to find which CPU
            // samples were taken inside that activity (or one nested under it), so the
            // main read below keeps only those. Async-correct: it consults each sample's
            // current start-stop activity, not a per-thread time window.
            string? activityName = scope?.ActivityName;
            HashSet<EventIndex>? activitySamples = string.IsNullOrEmpty(activityName)
                ? null
                : ComputeActivitySampleFilter(traceLog, symbolReader, activityName);

            return ReadCore(
                traceLog,
                symbolReader,
                scopePids,
                appliedScopeName,
                activitySamples,
                activityName,
                scope?.Window,
                cacheState,
                new SourceResolutionTracker(symbolsDirectory, localSymbolPath));
        }
        finally
        {
            if (extractedPdbDirectory is not null)
            {
                try
                {
                    Directory.Delete(extractedPdbDirectory, recursive: true);
                }
                catch (Exception)
                {
                    // Best-effort cleanup of a temp directory; a leftover under
                    // %TEMP% is harmless if the delete races a still-open handle.
                }
            }
        }
    }

    internal static string? NormalizeSymbolsDirectory(string? symbolsDirectory)
    {
        if (string.IsNullOrEmpty(symbolsDirectory))
        {
            return null;
        }

        if (!IsSingleLocalSymbolPath(symbolsDirectory))
        {
            throw new ArgumentException(
                "Symbols must be one local build-output directory; symbol-path syntax is not allowed.",
                nameof(symbolsDirectory));
        }

        string fullPath = Path.GetFullPath(symbolsDirectory);
        if (!IsSingleLocalSymbolPath(fullPath))
        {
            throw new ArgumentException(
                "Symbols must be one local build-output directory; symbol-path syntax is not allowed.",
                nameof(symbolsDirectory));
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Symbols directory '{fullPath}' was not found.");
        }

        return fullPath;
    }

    private static bool IsSingleLocalSymbolPath(string path)
    {
        SymbolPath symbolPath = new(path);
        return symbolPath.Elements.Count == 1 && !symbolPath.Elements.Single().IsRemote;
    }

    // Runs the start-stop activity computer over the trace and returns the set of CPU
    // sample events (by index) taken while a thread was inside an activity whose task
    // name matches, or inside one nested under it. This is a pre-pass over the same
    // TraceLog the main read then walks; consulting each sample's current start-stop
    // activity (rather than a per-thread time window) keeps it correct across the async
    // thread hops an activity makes.
    private static HashSet<EventIndex> ComputeActivitySampleFilter(
        TraceLog traceLog,
        SymbolReader symbolReader,
        string activityName)
    {
        using TraceLogEventSource source = traceLog.Events.GetSource();
        GCReferenceComputer gcReferences = new(source);
        ActivityComputer activityComputer = new(source, symbolReader, gcReferences);
        StartStopActivityComputer startStop = new(source, activityComputer, ignoreApplicationInsightsRequestsWithRelatedActivityId: false);

        HashSet<EventIndex> matching = [];
        source.AllEvents += data =>
        {
            if (data is not (SampledProfileTraceData or ClrThreadSampleTraceData))
            {
                return;
            }

            TraceThread? thread = data.Thread();
            if (thread is null)
            {
                return;
            }

            // GetCurrentStartStopActivity returns the innermost activity on the thread;
            // walk its parent (Creator) chain so a scope to an outer activity also keeps
            // samples taken inside its nested children.
            for (StartStopActivity? activity = startStop.GetCurrentStartStopActivity(thread, data);
                activity is not null;
                activity = activity.Creator)
            {
                if (string.Equals(activity.TaskName, activityName, StringComparison.OrdinalIgnoreCase))
                {
                    matching.Add(data.EventIndex);
                    break;
                }
            }
        };

        source.Process();
        return matching;
    }

    private static TraceReadResult ReadCore(
        TraceLog traceLog,
        SymbolReader symbolReader,
        HashSet<int>? scopePids,
        string? appliedScopeName,
        HashSet<EventIndex>? activitySamples,
        string? activityName,
        TimeWindow? window,
        EtlxCacheState cacheState,
        SourceResolutionTracker sourceResolution)
    {
        AnalysisEventCounter analysisEvents = new();
        Dictionary<int, string> locationCache = [];

        List<SampleStack> samples = [];
        long totalFrames = 0;
        long resolvedFrames = 0;
        List<string> leafToRoot = [];
        List<string> leafToRootLocations = [];

        foreach (TraceEvent data in traceLog.Events)
        {
            analysisEvents.Observe(data);

            // ETW (.etl) surfaces CPU samples as SampledProfileTraceData; EventPipe
            // (.nettrace) surfaces them as the SampleProfiler's ClrThreadSampleTraceData.
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

            // When scoped to a process tree, drop samples from any process outside it.
            // The trace is already fully resolved, so this is a lossless narrowing.
            if (scopePids is not null && !scopePids.Contains(data.ProcessID))
            {
                continue;
            }

            // When scoped to a time window, drop samples taken outside it; every sample
            // carries a trace-relative timestamp, so the same guard scopes every metric.
            if (window is TimeWindow timeScope && !timeScope.Contains(data.TimeStampRelativeMSec))
            {
                continue;
            }

            // When scoped to an activity, drop samples that were not taken inside it (the
            // matching set was computed by the activity pre-pass above).
            if (activitySamples is not null && !activitySamples.Contains(data.EventIndex))
            {
                continue;
            }

            TraceCallStack? callStack = data.CallStack();
            if (callStack is null)
            {
                continue;
            }

            leafToRoot.Clear();
            leafToRootLocations.Clear();
            for (TraceCallStack? frame = callStack; frame is not null; frame = frame.Caller)
            {
                TraceCodeAddress address = frame.CodeAddress;
                string method = address.FullMethodName;
                string module = address.ModuleName;

                totalFrames++;
                string name;
                if (string.IsNullOrEmpty(method))
                {
                    name = $"{(string.IsNullOrEmpty(module) ? "?" : module)}!?";
                }
                else
                {
                    resolvedFrames++;
                    name = string.IsNullOrEmpty(module) ? method : $"{module}!{method}";
                }

                leafToRoot.Add(name);
                string location = ResolveLocation(symbolReader, address, locationCache);
                leafToRootLocations.Add(location);
                sourceResolution.Observe(address, method, location.Length > 0);
            }

            if (leafToRoot.Count == 0)
            {
                continue;
            }

            int count = leafToRoot.Count;
            string[] frames = new string[count];
            string[] locations = new string[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = leafToRoot[count - 1 - i];
                locations[i] = leafToRootLocations[count - 1 - i];
            }

            // Tag the sample with its owning process so a multi-process trace can be
            // reasoned about per process; empty resolves to just the numeric id. IDs are
            // formatted invariantly so the labels stay ASCII-stable across locales.
            string processName = data.ProcessName;
            string pid = data.ProcessID.ToString(CultureInfo.InvariantCulture);
            string process = string.IsNullOrEmpty(processName) ? pid : $"{processName}({pid})";

            samples.Add(new SampleStack(
                frames,
                1.0,
                data.ThreadID.ToString(CultureInfo.InvariantCulture),
                locations,
                process));
        }

        double resolutionRate = totalFrames > 0 ? (double)resolvedFrames / totalFrames : 0.0;

        List<string> warnings = [];
        if (samples.Count == 0)
        {
            // An empty result can come from a scope dropping every sample or an absent
            // CPU sampler; name the most specific cause so the message does not blame the
            // capture when a scope is at fault. With more than one scope applied, name
            // them all - any one could be responsible - rather than guess.
            List<string> scopes = [];
            if (appliedScopeName is not null)
            {
                scopes.Add($"the '{appliedScopeName}' process tree");
            }

            if (activityName is not null)
            {
                scopes.Add($"the '{activityName}' activity");
            }

            if (window is TimeWindow emptyWindow && emptyWindow.IsBounded)
            {
                scopes.Add($"the {emptyWindow} window");
            }

            if (scopes.Count > 1)
            {
                warnings.Add(
                    $"No samples remained after scoping to {JoinScopes(scopes)}; "
                    + "a scope may have dropped them all - relax one to see which.");
            }
            else if (activityName is not null)
            {
                warnings.Add(
                    $"No samples remained inside the '{activityName}' activity; the trace may carry no such "
                    + "activity (activities come from EventSource Start/Stop events), or none of its samples were CPU samples.");
            }
            else if (window is TimeWindow soleWindow && soleWindow.IsBounded)
            {
                warnings.Add(
                    $"No samples remained inside the {soleWindow} window; the trace may carry no CPU samples "
                    + "there, or the window may lie outside the captured range - widen or drop it to check.");
            }
            else
            {
                warnings.Add(appliedScopeName is not null
                    ? $"No samples remained after scoping to the '{appliedScopeName}' process tree; "
                        + "the scope may match no process with samples - pass --all-processes to read every process."
                    : "No sampled-profile (CPU) events were found. Was the trace captured with a CPU sampler?");
            }
        }
        else
        {
            if (appliedScopeName is not null)
            {
                warnings.Add(
                    $"Scoped to the '{appliedScopeName}' process tree; pass --all-processes to read every process.");
            }

            if (activityName is not null)
            {
                warnings.Add($"Scoped to the '{activityName}' activity.");
            }

            if (window is TimeWindow appliedWindow && appliedWindow.IsBounded)
            {
                warnings.Add($"Scoped to the {appliedWindow} window.");
            }
        }

        if (SymbolGate.TryGetWarning(resolutionRate, samples.Count, out string? symbolWarning))
        {
            warnings.Add(symbolWarning);
        }

        analysisEvents.AddProcesses(traceLog.Processes.Count);

        return new TraceReadResult(
            samples,
            resolutionRate,
            warnings,
            StackRecordSemantics.PeriodicCpuSamples,
            cacheState,
            analysisEvents.Counts,
            sourceResolution.CreateInfo());
    }

    // Joins the applied-scope phrases into one clause: "A" for one, "A and B" for two,
    // and "A, B, and C" for three, so an empty-result warning can name every scope that
    // could have dropped the samples in readable prose.
    private static string JoinScopes(List<string> scopes) => scopes.Count switch
    {
        <= 1 => scopes.Count == 1 ? scopes[0] : string.Empty,
        2 => $"{scopes[0]} and {scopes[1]}",
        _ => $"{string.Join(", ", scopes.GetRange(0, scopes.Count - 1))}, and {scopes[^1]}"
    };

    /// <summary>
    ///  Points <paramref name="symbolReader"/> at the Microsoft public symbol server
    ///  (fronted by a local cache) and resolves the names of the unmanaged runtime
    ///  modules, so native runtime frames (the GC, the JIT, <c>memset</c> /
    ///  <c>memcpy</c>, write barriers) carry method names instead of a bare module
    ///  address.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Only the runtime/OS modules are looked up - <c>coreclr</c> / <c>clr</c>,
    ///   <c>clrjit</c>, <c>ntdll</c>, <c>kernelbase</c>, <c>kernel32</c>,
    ///   <c>ucrtbase</c>, <c>msvcrt</c> - rather than every loaded module, so the
    ///   download is bounded to the frames a runtime profile actually needs. Each
    ///   lookup is best-effort: a module whose PDB cannot be fetched (offline, or no
    ///   published symbols) simply keeps its unresolved frames rather than failing the
    ///   whole read.
    ///  </para>
    /// </remarks>
    private static void ResolveNativeRuntimeSymbols(
        TraceLog traceLog,
        SymbolReader symbolReader,
        SymbolOptions options)
    {
        string cacheDirectory = options.CacheDirectory ?? SymbolOptions.DefaultCacheDirectory;
        Directory.CreateDirectory(cacheDirectory);

        // The standard symbol-path form: a local downstream cache backed by the public
        // server, so the first read downloads and later reads hit the cache. Preserve
        // any path already set (the local build-output PDBs) by appending the server.
        string serverPath = $"srv*{cacheDirectory}*https://msdl.microsoft.com/download/symbols";
        SymbolPath symbolPath = new(symbolReader.SymbolPath);
        symbolPath.Add(serverPath);
        symbolReader.SymbolPath = symbolPath.ToString();

        foreach (TraceModuleFile moduleFile in traceLog.ModuleFiles)
        {
            if (!IsRuntimeModule(moduleFile.Name))
            {
                continue;
            }

            try
            {
                traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, moduleFile);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
            {
                // Best-effort: an unfetchable module keeps its unresolved frames rather
                // than failing the read. Offline use and modules with no published PDB
                // both land here. Fatal process-corruption conditions (out of memory,
                // access violation) are allowed to surface rather than being masked as
                // a merely-missing symbol.
            }
        }
    }

    /// <summary>
    ///  Whether <paramref name="moduleName"/> is one of the unmanaged .NET runtime or
    ///  OS modules whose symbols answer "where did the native time go" (GC, JIT,
    ///  memory operations), so native resolution can be bounded to them.
    /// </summary>
    private static bool IsRuntimeModule(string? moduleName)
    {
        if (string.IsNullOrEmpty(moduleName))
        {
            return false;
        }

        // Match the runtime/OS modules by name substring (case-insensitive); the
        // module name carries no extension here. `clr` is matched exactly because it is
        // a short token that would otherwise match unrelated names.
        foreach (string token in s_runtimeModuleTokens)
        {
            if (moduleName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return moduleName.Equals("clr", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] s_runtimeModuleTokens =
    [
        "coreclr",
        "clrjit",
        "ntdll",
        "kernelbase",
        "kernel32",
        "ucrtbase",
        "msvcrt"
    ];

    /// <summary>
    ///  Resolves the source location (<c>file:line</c>) for a code address,
    ///  caching the result by code-address index so repeated frames cost one
    ///  symbol lookup. Returns an empty string when no local PDB maps the
    ///  address to a source line.
    /// </summary>
    private static string ResolveLocation(SymbolReader reader, TraceCodeAddress address, Dictionary<int, string> cache)
    {
        int key = (int)address.CodeAddressIndex;
        if (cache.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        string location = "";
        try
        {
            SourceLocation? source = address.GetSourceLine(reader);
            if (source?.SourceFile is { } file)
            {
                location = $"{Path.GetFileName(file.BuildTimeFilePath)}:{source.LineNumber}";
            }
        }
        catch (Exception)
        {
            // Symbol resolution is best-effort; an unresolved frame carries no line.
        }

        cache[key] = location;
        return location;
    }
}
