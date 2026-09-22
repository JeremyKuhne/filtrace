// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal abstract partial class TraceLogReader
{
    private const int StackCacheProbeSampleCount = 512;
    private const int MaximumCachedSampleStacks = 4096;
    private const int MaximumCachedFrameIdentities = SourceResolutionTracker.MaxTrackedMethods;

    /// <summary>
    ///  Determines whether a bounded probe has enough repeated stack indices to amortize retained frame lists.
    /// </summary>
    /// <param name="sampleCount">The number of selected samples in the completed probe.</param>
    /// <param name="distinctStackCount">The number of distinct call-stack indices in those samples.</param>
    /// <returns>
    ///  <see langword="true"/> when the probe is complete and at least half of its samples repeat a stack index.
    /// </returns>
    internal static bool ShouldCacheSampleStacks(int sampleCount, int distinctStackCount) =>
        sampleCount >= StackCacheProbeSampleCount
            && distinctStackCount <= sampleCount / 2;

    /// <summary>
    ///  Determines whether another trace-local stack index can be tracked without exceeding the cache bound.
    /// </summary>
    /// <param name="cachedStackCount">The current number of tracked stack indices.</param>
    /// <returns><see langword="true"/> when another index can be tracked.</returns>
    internal static bool CanTrackSampleStack(int cachedStackCount) =>
        cachedStackCount < MaximumCachedSampleStacks;

    /// <summary>
    ///  Determines whether another rendered frame identity can be retained for reuse and aggregate accounting.
    /// </summary>
    /// <param name="cachedFrameCount">The current number of retained frame identities.</param>
    /// <returns><see langword="true"/> when another identity can be retained.</returns>
    internal static bool CanCacheFrameIdentity(int cachedFrameCount) =>
        cachedFrameCount < MaximumCachedFrameIdentities;

    private static Dictionary<int, CachedSampleStack?>? CreateSampleStackCache(
        int sampleCount,
        HashSet<int> observedStackIndexes)
    {
        if (!ShouldCacheSampleStacks(sampleCount, observedStackIndexes.Count))
        {
            return null;
        }

        Dictionary<int, CachedSampleStack?> cache = new(observedStackIndexes.Count);
        foreach (int stackIndex in observedStackIndexes)
        {
            cache.Add(stackIndex, value: null);
        }

        return cache;
    }

    private static void ObserveCachedStackReuses(
        EtlxTraceLog traceLog,
        Dictionary<int, CachedSampleStack?>? sampleStackCache,
        Dictionary<FrameIdentity, FrameNameCacheEntry> frameNameCache,
        Dictionary<int, string>? locationCache,
        SourceResolutionTracker sourceResolution)
    {
        if (sampleStackCache is null)
        {
            return;
        }

        foreach (KeyValuePair<int, CachedSampleStack?> pair in sampleStackCache)
        {
            CachedSampleStack? cachedStack = pair.Value;
            if (cachedStack is null || cachedStack.ReusedSampleCount == 0)
            {
                continue;
            }

            for (CallStackIndex frameIndex = (CallStackIndex)pair.Key;
                frameIndex != CallStackIndex.Invalid;
                frameIndex = traceLog.CallStacks.Caller(frameIndex))
            {
                TraceCodeAddress address = traceLog.CodeAddresses[traceLog.CallStacks.CodeAddressIndex(frameIndex)];
                TraceMethod? traceMethod = address.Method;
                if (traceMethod is null)
                {
                    continue;
                }

                TraceModuleFile? addressModule = address.ModuleFile;
                TraceModuleFile? methodModule = traceMethod.MethodModuleFile ?? addressModule;
                string method = traceMethod.FullMethodName;
                string module = methodModule?.Name ?? addressModule?.Name ?? string.Empty;
                int methodKey = (int)traceMethod.MethodIndex;
                FrameIdentity frameIdentity = new(
                    (int)(addressModule?.ModuleFileIndex ?? ModuleFileIndex.Invalid),
                    methodKey);

                bool sourceMapped = locationCache is not null
                    && locationCache.TryGetValue((int)address.CodeAddressIndex, out string? location)
                    && location.Length > 0;

                if (frameNameCache.TryGetValue(frameIdentity, out FrameNameCacheEntry entry))
                {
                    entry.Observe(
                        cachedStack.ReusedSampleCount,
                        sourceMapped ? cachedStack.ReusedSampleCount : 0);

                    frameNameCache[frameIdentity] = entry;
                }
                else
                {
                    sourceResolution.ObserveManagedFrameCounts(
                        methodKey,
                        methodModule,
                        module,
                        method,
                        sourceMapped ? cachedStack.ReusedSampleCount : 0,
                        sourceMapped ? 0 : cachedStack.ReusedSampleCount);
                }
            }
        }
    }

    private static string GetFrameName(
        Dictionary<(string Module, string Method), string> canonicalFrameNames,
        string module,
        string method,
        bool retain)
    {
        if (canonicalFrameNames.TryGetValue((module, method), out string? name))
        {
            return name;
        }

        name = string.IsNullOrEmpty(method)
            ? $"{(string.IsNullOrEmpty(module) ? "?" : module)}!?"
            : string.IsNullOrEmpty(module) ? method : $"{module}!{method}";

        if (retain)
        {
            canonicalFrameNames.Add((module, method), name);
        }

        return name;
    }
}