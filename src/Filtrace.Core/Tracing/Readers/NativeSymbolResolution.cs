// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Applies locally available native symbols to the modules that account for the most
///  unresolved sampled frames.
/// </summary>
/// <remarks>
///  <para>
///   TraceEvent resolves managed methods from the CLR rundown embedded in the trace, but
///   a native module carries no rundown - its frames stay unresolved until something asks
///   TraceEvent to load that module's PDB. Native runtime resolution covers a fixed
///   allowlist of runtime and OS modules against the public symbol server; this covers
///   the complementary case, where the module is product-specific (a Native AOT binary,
///   a C++ dependency) and its PDB is sitting in the directory the caller supplied.
///  </para>
///  <para>
///   The pass is ordered before any symbol-server element is appended to the reader's
///   symbol path, so every lookup here is satisfied from local disk and the pass stays
///   offline regardless of whether native runtime resolution later runs.
///  </para>
/// </remarks>
internal static class NativeSymbolResolution
{
    /// <summary>
    ///  The smallest share of unresolved sampled frames worth spending a lookup on.
    ///  Bounding by share rather than module count keeps the work proportional to what
    ///  the profile actually cannot explain, so a trace with one dominant unresolved
    ///  module does not pay for a long tail of incidental ones.
    /// </summary>
    private const double MinimumUnresolvedShare = 0.01;

    /// <summary>
    ///  How many modules each reported category carries, matching the bound the managed
    ///  source diagnostics use so a pathological trace cannot flood the output.
    /// </summary>
    private const int MaxReportedModules = 8;

    /// <summary>
    ///  Summarizes a lookup pass for reporting, or <see langword="null"/> when the trace
    ///  had no unresolved native frames and the pass therefore says nothing.
    /// </summary>
    /// <param name="statuses">Per-module outcomes from <see cref="ResolveLocal"/>.</param>
    /// <returns>
    ///  Bounded outcome categories and unresolved-frame count, or <see langword="null"/> when no lookup ran.
    /// </returns>
    public static NativeSymbolInfo? CreateInfo(IReadOnlyList<NativeModuleSymbolStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return null;
        }

        int unresolvedFrames = 0;
        foreach (NativeModuleSymbolStatus status in statuses)
        {
            unresolvedFrames = SaturatingAdd(unresolvedFrames, status.UnresolvedFrames);
        }

        return new NativeSymbolInfo(
            Describe(statuses, NativeSymbolStatus.Resolved, "frames"),
            Describe(statuses, NativeSymbolStatus.NoSymbolFile, "unresolved"))
        {
            IdentityMismatchModules = Describe(statuses, NativeSymbolStatus.IdentityMismatch, "unresolved"),
            LookupFailedModules = Describe(statuses, NativeSymbolStatus.LookupFailed, "unresolved"),
            UnresolvedFrameCount = unresolvedFrames
        };
    }

    /// <summary>
    ///  Renders the highest-impact modules in one status category. The statuses arrive
    ///  ordered by descending unresolved frames, so taking the first entries keeps the
    ///  ones that matter.
    /// </summary>
    /// <param name="statuses">Per-module outcomes to render from.</param>
    /// <param name="status">The category to include.</param>
    /// <param name="noun">
    ///  How to label the count. A resolved module's frames are no longer unresolved, so
    ///  reporting them as such would contradict the category they are listed under.
    /// </param>
    private static string[] Describe(
        IReadOnlyList<NativeModuleSymbolStatus> statuses,
        NativeSymbolStatus status,
        string noun)
    {
        List<string> described = [];
        foreach (NativeModuleSymbolStatus candidate in statuses)
        {
            if (candidate.Status != status)
            {
                continue;
            }

            // An integer percent keeps the entry culture-independent and short; the exact
            // share matters less than which module dominates.
            int percent = (int)Math.Round(candidate.UnresolvedShare * 100);
            described.Add($"{candidate.ModuleName} ({candidate.UnresolvedFrames} {noun}, {percent}%)");
            if (described.Count == MaxReportedModules)
            {
                break;
            }
        }

        return [.. described];
    }

    private static int SaturatingAdd(int left, int right)
    {
        long sum = (long)left + right;
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }

    /// <summary>
    ///  Looks up local symbols for the modules carrying the most unresolved sampled
    ///  frames and reports what each lookup found.
    /// </summary>
    /// <param name="traceLog">The trace to resolve against.</param>
    /// <param name="symbolReader">The reader, whose symbol path must not yet carry a symbol-server element.</param>
    /// <param name="symbolsDirectory">The directory the caller supplied, for reason reporting.</param>
    /// <returns>
    ///  One entry per module that had unresolved sampled frames, ordered by descending
    ///  unresolved frame count. Empty when the trace has no unresolved native frames.
    /// </returns>
    public static IReadOnlyList<NativeModuleSymbolStatus> ResolveLocal(
        TraceLog traceLog,
        SymbolReader symbolReader,
        string? symbolsDirectory)
    {
        // Cheap gate: a scan of unique code addresses is far smaller than a walk of every
        // sampled frame, so a trace whose frames all resolved never pays for the ranking
        // pass below.
        Dictionary<int, int> unresolvedAddresses = CountUnresolvedAddressesByModule(traceLog);
        if (unresolvedAddresses.Count == 0)
        {
            return [];
        }

        Dictionary<int, int> unresolvedFrames = CountUnresolvedFramesByModule(traceLog, unresolvedAddresses);
        if (unresolvedFrames.Count == 0)
        {
            return [];
        }

        long totalUnresolved = 0;
        foreach (int count in unresolvedFrames.Values)
        {
            totalUnresolved += count;
        }

        if (totalUnresolved == 0)
        {
            return [];
        }

        List<TraceModuleFile> ranked = [];
        foreach (TraceModuleFile moduleFile in traceLog.ModuleFiles)
        {
            if (unresolvedFrames.ContainsKey((int)moduleFile.ModuleFileIndex))
            {
                ranked.Add(moduleFile);
            }
        }

        ranked.Sort((left, right) =>
            unresolvedFrames[(int)right.ModuleFileIndex].CompareTo(unresolvedFrames[(int)left.ModuleFileIndex]));

        List<NativeModuleSymbolStatus> statuses = new(ranked.Count);
        foreach (TraceModuleFile moduleFile in ranked)
        {
            int frames = unresolvedFrames[(int)moduleFile.ModuleFileIndex];
            double share = (double)frames / totalUnresolved;
            NativeSymbolStatus status = share < MinimumUnresolvedShare
                ? NativeSymbolStatus.NotAttempted
                : Attempt(traceLog, symbolReader, moduleFile, symbolsDirectory);

            statuses.Add(new NativeModuleSymbolStatus(moduleFile.Name ?? "?", status, frames, share));
        }

        return statuses;
    }

    /// <summary>
    ///  Attempts the lookup for one module and classifies the outcome.
    /// </summary>
    private static NativeSymbolStatus Attempt(
        TraceLog traceLog,
        SymbolReader symbolReader,
        TraceModuleFile moduleFile,
        string? symbolsDirectory)
    {
        int before = CountUnresolvedAddressesInModule(traceLog, moduleFile);

        try
        {
            traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, moduleFile);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        {
            // Best-effort, matching native runtime resolution: a module whose symbols
            // cannot be loaded keeps its unresolved frames rather than failing the read.
            return NativeSymbolStatus.LookupFailed;
        }

        if (CountUnresolvedAddressesInModule(traceLog, moduleFile) < before)
        {
            return NativeSymbolStatus.Resolved;
        }

        return ClassifyMiss(moduleFile, symbolsDirectory);
    }

    /// <summary>
    ///  Separates "no symbol file at all" from "a symbol file with the right name whose
    ///  identity does not match", which are different problems for the caller to fix.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   The lookup has already failed by this point, so the presence of a file under the
    ///   module's recorded PDB name is what distinguishes the two: the file was there and
    ///   did not satisfy the module. This deliberately does not consult
    ///   <see cref="SymbolReader.FindSymbolFilePath"/> - it returns a path for a
    ///   same-named PDB whose identity does not match, so treating its result as a
    ///   verdict reports a mismatch as a missing file.
    ///  </para>
    /// </remarks>
    private static NativeSymbolStatus ClassifyMiss(
        TraceModuleFile moduleFile,
        string? symbolsDirectory)
    {
        string? pdbName = moduleFile.PdbName;
        if (string.IsNullOrEmpty(pdbName) || string.IsNullOrEmpty(symbolsDirectory))
        {
            return NativeSymbolStatus.NoSymbolFile;
        }

        return NameExistsLocally(symbolsDirectory, pdbName)
            ? NativeSymbolStatus.IdentityMismatch
            : NativeSymbolStatus.NoSymbolFile;
    }

    /// <summary>
    ///  Whether a file named <paramref name="pdbName"/> exists anywhere under
    ///  <paramref name="symbolsDirectory"/>.
    /// </summary>
    private static bool NameExistsLocally(string symbolsDirectory, string pdbName)
    {
        try
        {
            string fileName = Path.GetFileName(pdbName);
            return fileName.Length > 0
                && Directory.EnumerateFiles(symbolsDirectory, fileName, SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        {
            // An unreadable or vanished directory is reported as a plain miss.
            return false;
        }
    }

    /// <summary>
    ///  Counts unique code addresses with no resolved method, keyed by module index.
    /// </summary>
    private static Dictionary<int, int> CountUnresolvedAddressesByModule(TraceLog traceLog)
    {
        Dictionary<int, int> counts = [];
        TraceCodeAddresses codeAddresses = traceLog.CodeAddresses;
        int total = codeAddresses.Count;
        for (int index = 0; index < total; index++)
        {
            TraceCodeAddress address = codeAddresses[(CodeAddressIndex)index];
            if (address.Method is not null)
            {
                continue;
            }

            TraceModuleFile? moduleFile = address.ModuleFile;
            if (moduleFile is null)
            {
                continue;
            }

            int key = (int)moduleFile.ModuleFileIndex;
            counts[key] = counts.TryGetValue(key, out int existing) ? existing + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    ///  Counts unique code addresses with no resolved method in one module.
    /// </summary>
    private static int CountUnresolvedAddressesInModule(TraceLog traceLog, TraceModuleFile moduleFile)
    {
        int count = 0;
        TraceCodeAddresses codeAddresses = traceLog.CodeAddresses;
        int total = codeAddresses.Count;
        for (int index = 0; index < total; index++)
        {
            TraceCodeAddress address = codeAddresses[(CodeAddressIndex)index];
            if (address.Method is null && address.ModuleFile?.ModuleFileIndex == moduleFile.ModuleFileIndex)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///  Weights the candidate modules by how many sampled frames they leave unresolved,
    ///  which is what determines whether a module darkens the profile - a module with few
    ///  distinct addresses can still dominate the samples.
    /// </summary>
    private static Dictionary<int, int> CountUnresolvedFramesByModule(
        TraceLog traceLog,
        Dictionary<int, int> candidates)
    {
        Dictionary<int, int> counts = [];
        foreach (TraceEvent data in traceLog.Events)
        {
            if (data is not (SampledProfileTraceData or ClrThreadSampleTraceData))
            {
                continue;
            }

            for (TraceCallStack? frame = data.CallStack(); frame is not null; frame = frame.Caller)
            {
                TraceCodeAddress address = frame.CodeAddress;
                if (address.Method is not null)
                {
                    continue;
                }

                TraceModuleFile? moduleFile = address.ModuleFile;
                if (moduleFile is null)
                {
                    continue;
                }

                int key = (int)moduleFile.ModuleFileIndex;
                if (!candidates.ContainsKey(key))
                {
                    continue;
                }

                counts[key] = counts.TryGetValue(key, out int existing) ? existing + 1 : 1;
            }
        }

        return counts;
    }
}
