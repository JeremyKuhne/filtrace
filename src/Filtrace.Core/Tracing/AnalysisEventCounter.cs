// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Filtrace.Tracing;

/// <summary>
///  Counts capture-wide source records for each analysis during an existing
///  TraceLog event pass.
/// </summary>
internal sealed class AnalysisEventCounter
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> Counts => _counts;

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

    public void AddProcesses(int count)
    {
        if (count > 0)
        {
            _counts["processes"] = count;
        }
    }

    private static bool IsApplicationProvider(string providerName) =>
        !providerName.StartsWith("Microsoft-Windows-DotNETRuntime", StringComparison.Ordinal)
        && !providerName.StartsWith("Microsoft-DotNETCore-", StringComparison.Ordinal)
        && !providerName.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal);

    private void Increment(string analysis)
    {
        _counts.TryGetValue(analysis, out int count);
        _counts[analysis] = checked(count + 1);
    }
}