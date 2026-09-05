// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information


namespace Filtrace.Tracing;

/// <summary>
///  Counts capture-wide source records for each analysis during an existing
///  TraceLog event pass.
/// </summary>
internal sealed class AnalysisEventCounter
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>
    ///  Gets the observed record counts keyed by canonical analysis name.
    /// </summary>
    public IReadOnlyDictionary<string, int> Counts => _counts;

    /// <summary>
    ///  Classifies one trace event and increments every analysis whose source records it represents.
    /// </summary>
    /// <param name="data">The event to classify.</param>
    public void Observe(TraceEvent data)
    {
        Increment("events");

        switch (data)
        {
            case SampledProfileTraceData:
            case ClrThreadSampleTraceData { Type: not ClrThreadSampleType.Error }:
                Increment("cpu");
                Increment("classify");
                break;

            case GCAllocationTickTraceData allocation when allocation.AllocationAmount64 > 0:
                Increment("alloc");
                break;

            case ExceptionTraceData:
                Increment("exceptions");
                break;

            case GCStartTraceData:
                Increment("gcstats");
                break;

            case MethodJittingStartedTraceData:
                Increment("jitstats");
                break;

            case ThreadPoolWorkerThreadAdjustmentTraceData:
                Increment("threadpool");
                break;

            case CSwitchTraceData:
                Increment("threadtime");
                break;
        }

        if (string.Equals(data.EventName, "Contention/Stop", StringComparison.Ordinal))
        {
            Increment("contention");
        }
        else if (string.Equals(data.EventName, "WaitHandleWait/Stop", StringComparison.Ordinal))
        {
            Increment("wait");
        }

        if (data.Opcode == TraceEventOpcode.Stop && IsApplicationProvider(data.ProviderName))
        {
            Increment("activity");
        }

        if (data.ProviderName.Contains("Kernel-Disk", StringComparison.OrdinalIgnoreCase)
            || data.EventName.StartsWith("DiskIO/", StringComparison.OrdinalIgnoreCase))
        {
            Increment("diskio");
        }
    }

    /// <summary>
    ///  Records the capture's process count when at least one process was observed.
    /// </summary>
    /// <param name="count">The number of processes in the trace.</param>
    public void AddProcesses(int count)
    {
        if (count > 0)
        {
            _counts["processes"] = count;
        }
    }

    /// <summary>
    ///  Determines whether an event provider is outside the known .NET runtime provider families.
    /// </summary>
    /// <param name="providerName">The provider name to classify.</param>
    /// <returns><see langword="true"/> for an application provider; otherwise <see langword="false"/>.</returns>
    internal static bool IsApplicationProvider(string providerName) =>
        !providerName.StartsWith("Microsoft-Windows-DotNETRuntime", StringComparison.Ordinal)
            && !providerName.StartsWith("Microsoft-DotNETCore-", StringComparison.Ordinal);

    private void Increment(string analysis)
    {
        _counts.TryGetValue(analysis, out int count);
        _counts[analysis] = SaturatingIncrement(count);
    }

    /// <summary>
    ///  Increments a nonnegative count while preserving <see cref="int.MaxValue"/> as the saturation point.
    /// </summary>
    /// <param name="count">The current count.</param>
    /// <returns>The incremented count, or <see cref="int.MaxValue"/> when already saturated.</returns>
    internal static int SaturatingIncrement(int count) =>
        count == int.MaxValue ? int.MaxValue : count + 1;
}
