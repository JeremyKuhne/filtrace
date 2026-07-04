// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using Filtrace.Caching;
using Filtrace.Tracing;

namespace Filtrace.Server;

/// <summary>
///  Loads traces on demand and caches the parsed model per absolute path, so
///  repeated queries against the same trace avoid re-parsing.
/// </summary>
/// <remarks>
///  <para>
///   The cache is a bounded least-recently-used cache: a long agent session can
///   touch many traces, and each parsed model is potentially large, so the store
///   retains only the most recently used traces rather than growing without limit.
///  </para>
/// </remarks>
public sealed class TraceStore
{
    /// <summary>
    ///  The maximum number of parsed traces retained before the least-recently-used
    ///  one is evicted.
    /// </summary>
    public const int DefaultCapacity = 16;

    private readonly TraceLoader _loader = new();

    // Match the cache's path comparison to the host file system: Windows and macOS
    // are case-insensitive, Linux is case-sensitive, so distinct-by-case paths must
    // not be conflated there.
    private readonly LruCache<string, LoadedTrace> _cache;

    /// <summary>
    ///  Initializes a new <see cref="TraceStore"/> retaining at most
    ///  <see cref="DefaultCapacity"/> traces.
    /// </summary>
    public TraceStore()
        : this(DefaultCapacity)
    {
    }

    /// <summary>
    ///  Initializes a new <see cref="TraceStore"/> retaining at most
    ///  <paramref name="capacity"/> traces.
    /// </summary>
    /// <param name="capacity">The maximum number of parsed traces to retain. Must be positive.</param>
    internal TraceStore(int capacity) =>
        _cache = new LruCache<string, LoadedTrace>(
            capacity,
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///  Returns the loaded trace for <paramref name="path"/>, loading and caching
    ///  it on first use.
    /// </summary>
    /// <param name="path">The trace file path.</param>
    /// <param name="symbolsDirectory">
    ///  Optional build-output directory whose assemblies' embedded portable PDBs are
    ///  extracted to resolve managed frames to <c>file:line</c>. Consumed only by the
    ///  CPU metric; the other providers resolve frames from the trace's own rundown
    ///  and ignore it. The cache keys on it for the CPU metric, so the same trace
    ///  loaded with and without symbols is cached separately.
    /// </param>
    /// <param name="metric">
    ///  Which provider's view to load: the CPU sampler's stacks (the default), the
    ///  allocation sites, and so on. The cache keys on it, so the same trace's CPU
    ///  and allocation views are cached separately.
    /// </param>
    /// <param name="scope">
    ///  Optional scope (an explicit process name or the automatic busiest-process
    ///  default, an activity task name, and/or a time window). The process scope is
    ///  consumed by the CPU and thread-time metrics and the activity scope by the CPU
    ///  metric only; the time window is consumed by every metric, since every sampled
    ///  event carries a timestamp. The cache keys on whichever axes a metric honors, so
    ///  the same trace scoped two ways is cached separately.
    /// </param>
    /// <param name="symbolOptions">
    ///  Optional native-symbol resolution. Consumed only by the CPU metric; the cache
    ///  keys on it, so the same trace read with and without native symbols is cached
    ///  separately. <see langword="null"/> is the managed-only offline default.
    /// </param>
    /// <returns>The cached loaded trace.</returns>
    public LoadedTrace Get(
        string path,
        string? symbolsDirectory = null,
        TraceMetric metric = TraceMetric.Cpu,
        ScopeRequest? scope = null,
        SymbolOptions? symbolOptions = null)
    {
        string fullPath = Path.GetFullPath(path);

        // Only the CPU loader consumes symbolsDirectory; the other providers resolve
        // frames from the trace's own rundown and ignore it. Drop it for those metrics
        // so two calls that differ only in an ignored symbols directory dedupe to one
        // cache entry instead of forcing a redundant provider read - and, for thread
        // time, a redundant ETLX conversion.
        string? fullSymbols = metric == TraceMetric.Cpu && !string.IsNullOrEmpty(symbolsDirectory)
            ? Path.GetFullPath(symbolsDirectory)
            : null;

        // The process scope narrows a multi-process capture, which only the CPU and
        // thread-time metrics read; the activity scope narrows the CPU reader alone
        // (thread time ignores it), so it keys the CPU metric only. Drop both for the
        // single-process EventPipe metrics so their entries are not split by an ignored
        // scope.
        string scopeKey = metric switch
        {
            TraceMetric.Cpu => ScopeKey(scope, includeActivity: true),
            TraceMetric.ThreadTime => ScopeKey(scope, includeActivity: false),
            _ => "-"
        };

        // The time window scopes every metric - every sampled event carries a timestamp -
        // so it keys all of them: a trace read for one window must not serve another.
        string timeKey = TimeKey(scope?.Window);

        // Only the CPU loader consumes symbolOptions (native resolution), so a trace
        // read with native symbols caches separately from one without; the other
        // metrics ignore it and share the "managed" fragment.
        string symbolKey = metric == TraceMetric.Cpu
            ? (symbolOptions ?? SymbolOptions.None).CacheKeyFragment()
            : "managed";

        // Length-prefix the first path so the two components cannot be confused for a
        // different pair: '|' - like every other ASCII separator - is a legal POSIX
        // file-name character, so a plain "a|b" delimiter could collide ("a|b" + "c"
        // versus "a" + "b|c"). The metric, scope, time, and symbol prefixes keep a
        // trace's distinct provider views, scopes, windows, and symbol modes from
        // sharing one cache entry. Loading uses the normalized symbols path so a relative
        // symbolsDirectory resolves exactly the way it was keyed.
        string key = $"{(int)metric}:{scopeKey}:{timeKey}:{symbolKey}:{fullPath.Length}|{fullPath}{fullSymbols}";
        return _cache.GetOrAdd(key, _ => _loader.Load(fullPath, metric, fullSymbols, scope, symbolOptions));
    }

    // A stable cache-key fragment for a scope request: the process axis ('all' for
    // all-processes, 'auto' for the automatic busiest-process default - a null request
    // is unspecified, the same default - or the explicit process name) followed by the
    // activity axis ('-' for none or when not keyed, or the activity task name). Because
    // the load path treats a null request as the automatic default, null and
    // ScopeRequest.Auto resolve to the same trace and so share the 'auto' fragment by
    // design. Both names are length-prefixed so they cannot be confused with the
    // sentinels, each other, or the following key segment. The activity is keyed only
    // when includeActivity is set (the CPU metric): the CPU reader filters samples by it,
    // so a trace scoped to an activity must not serve an unscoped read; thread time
    // ignores it, so keying it there would only fragment the cache.
    private static string ScopeKey(ScopeRequest? scope, bool includeActivity)
    {
        string activity = includeActivity && scope?.ActivityName is string name && name.Length > 0
            ? $"a{name.Length}:{name}"
            : "-";

        if (scope is null || (scope.ProcessName is null && !scope.IncludeAll))
        {
            return $"auto:{activity}";
        }

        if (scope.IncludeAll)
        {
            return $"all:{activity}";
        }

        string processName = scope.ProcessName!;
        return $"p{(scope.IncludeChildren ? "+" : "-")}{processName.Length}:{processName}:{activity}";
    }

    // A stable cache-key fragment for a time window: '-' when unbounded (no time scope),
    // otherwise the round-trippable start and end bounds ('*' for an open bound). Keyed
    // for every metric because the window scopes every metric, so a trace read for one
    // window must not serve another.
    private static string TimeKey(TimeWindow? window)
    {
        if (window is not TimeWindow bounded || !bounded.IsBounded)
        {
            return "-";
        }

        string start = bounded.StartMSec is double lower ? lower.ToString("R", CultureInfo.InvariantCulture) : "*";
        string end = bounded.EndMSec is double upper ? upper.ToString("R", CultureInfo.InvariantCulture) : "*";
        return $"t{start},{end}";
    }
}
