// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing.Readers;

namespace Filtrace.Tracing.Providers;

/// <summary>
///  Reads CPU ownership by process, scanning ETL and EventPipe samples without
///  materializing call-stack frames and using the full stack reader for speedscope.
/// </summary>
public sealed class ProcessInventoryProvider
{
    /// <summary>
    ///  Reads every process represented by CPU samples in <paramref name="path"/>.
    ///  ETL and EventPipe inputs use the direct sample scan; speedscope uses the
    ///  existing full-reader path.
    /// </summary>
    /// <param name="path">The trace path.</param>
    /// <returns>The process inventory and CPU-weight provenance.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The trace does not exist.</exception>
    /// <exception cref="NotSupportedException">The trace format is unsupported.</exception>
    public ProcessInventorySnapshot Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Trace file not found: {fullPath}", fullPath);
        }

        if (fullPath.EndsWith(".speedscope.json", StringComparison.OrdinalIgnoreCase))
        {
            LoadedTrace trace = new TraceLoader().Load(
                fullPath,
                scope: ScopeRequest.AllProcesses);

            return new ProcessInventorySnapshot(
                fullPath,
                TraceFormat.Speedscope,
                trace.Aggregator.Processes(),
                trace.Aggregator.Metric,
                trace.Info.CpuSampling,
                trace.Info.Warnings,
                EtlxCacheState: null);
        }

        TraceFormat format = fullPath.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)
            ? TraceFormat.Etl
            : fullPath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase)
                ? TraceFormat.NetTrace
                : throw new NotSupportedException(
                    $"Unrecognized trace format for '{fullPath}'. Supported: .speedscope.json, .nettrace, .etl");

        using EtlxTraceLog traceLog = TraceConverter.OpenTraceLog(
            fullPath,
            out EtlxCacheState cacheState);

        return ReadTraceLog(fullPath, format, traceLog, cacheState);
    }

    private static ProcessInventorySnapshot ReadTraceLog(
        string path,
        TraceFormat format,
        EtlxTraceLog traceLog,
        EtlxCacheState cacheState)
    {
        Dictionary<int, ProcessLabelCacheEntry> processLabels = [];
        Dictionary<string, (int Count, double Weight)> byProcess = new(StringComparer.Ordinal);
        CpuSampleWeighting? cpuWeighting = format == TraceFormat.Etl ? new() : null;
        CpuSampleEvidence cpuSamples = default;
        int totalSamples = 0;

        foreach (TraceEvent data in traceLog.Events)
        {
            if (data is SampledProfileIntervalTraceData interval)
            {
                cpuWeighting?.ObserveInterval(
                    (int)interval.Opcode,
                    interval.SampleSource,
                    interval.NewInterval);

                continue;
            }

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

            if (data.CallStackIndex() == CallStackIndex.Invalid)
            {
                continue;
            }

            int processId = data.ProcessID;
            string processName = data.ProcessName;
            if (!processLabels.TryGetValue(processId, out ProcessLabelCacheEntry? processEntry)
                && TraceLogReader.CanCacheProcessLabel(processLabels.Count))
            {
                processEntry = new ProcessLabelCacheEntry(processId, processName);
                processLabels.Add(processId, processEntry);
            }

            string process = processEntry?.GetLabel(processName)
                ?? ProcessLabelCacheEntry.CreateLabel(processId, processName);

            (int Count, double Weight) accumulated = byProcess.GetValueOrDefault(process);
            double weight = cpuSamples.GetSampleWeight(cpuWeighting, data is ClrThreadSampleTraceData);
            byProcess[process] = (
                AnalysisEventCounter.SaturatingIncrement(accumulated.Count),
                accumulated.Weight + weight);

            totalSamples = AnalysisEventCounter.SaturatingIncrement(totalSamples);
        }

        CpuSampleProvenance cpuSampling = cpuSamples.CreateProvenance(cpuWeighting, totalSamples);
        bool timeWeightsEstablished = cpuSampling.TimeWeightsEstablished;
        MetricInfo metric = timeWeightsEstablished ? MetricInfo.Cpu : MetricInfo.CpuSamples;
        List<ProcessSummary> processes = new(byProcess.Count);
        double totalWeight = 0.0;
        foreach (KeyValuePair<string, (int Count, double Weight)> entry in byProcess)
        {
            double weight = timeWeightsEstablished ? entry.Value.Weight : entry.Value.Count;
            totalWeight += weight;
            processes.Add(new ProcessSummary(entry.Key, entry.Value.Count, weight, PercentOfScope: 0.0));
        }

        for (int index = 0; index < processes.Count; index++)
        {
            ProcessSummary process = processes[index];
            double percent = totalWeight > 0.0 ? process.Weight / totalWeight * 100.0 : 0.0;
            processes[index] = process with { PercentOfScope = percent };
        }

        processes.Sort(static (left, right) =>
        {
            int byWeight = right.Weight.CompareTo(left.Weight);
            return byWeight != 0
                ? byWeight
                : string.CompareOrdinal(left.Process, right.Process);
        });

        List<string> warnings = [];
        TraceLogReader.AddEventLossWarning(warnings, traceLog.EventsLost);
        cpuSamples.AddWarnings(warnings, cpuWeighting, cpuSampling, totalSamples);
        _ = CaptureMetadataReader.Read(path, warnings);
        if (totalSamples == 0)
        {
            warnings.Add(
                "No sampled-profile (CPU) events were found. Was the trace captured with a CPU sampler?");
        }

        return new ProcessInventorySnapshot(
            path,
            format,
            new ProcessListResult(totalWeight, totalSamples, processes),
            metric,
            cpuSampling,
            warnings,
            cacheState);
    }

}
