// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Runs a timeline request against the analysis core: parse and validate the
///  time-window and lane selectors, clamp the bucket count, read the per-bucket
///  activity for each requested lane, wrap it in the output contract, and render it
///  as text or JSON.
/// </summary>
/// <remarks>
///  <para>
///   The timeline is an orientation view, not a stack ranking, and it spans both
///   trace formats (EventPipe <c>.nettrace</c> and ETW <c>.etl</c>), so it reads
///   through the dual-format guardrail like the raw event query. Every parse and
///   validation decision lives here rather than in the verb handler, so the executor
///   can be driven directly in tests.
///  </para>
/// </remarks>
internal static class TimelineExecutor
{
    /// <summary>
    ///  Executes the timeline request.
    /// </summary>
    /// <param name="request">The validated timeline inputs.</param>
    /// <param name="output">The writer the result is rendered to.</param>
    /// <param name="error">The writer usage and load errors are reported to.</param>
    /// <returns>A process exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Run(TimelineRequest request, TextWriter output, TextWriter error)
    {
        if (!TimeWindow.TryParse(request.Time, out double? startMSec, out double? endMSec, out string? timeError))
        {
            error.WriteLine(timeError);
            return ExitCodes.UsageError;
        }

        if (!TimelineProvider.TryResolveLanes(request.Lanes, out IReadOnlyList<string> lanes, out string? laneError))
        {
            error.WriteLine(laneError);
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveScope(request.Process, request.AllProcesses, out ScopeRequest scope, out string? scopeError))
        {
            error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        List<string> warnings = [];
        int buckets = TimelineProvider.ClampBucketCount(request.BucketCount, out string? bucketWarning);
        if (bucketWarning is not null)
        {
            warnings.Add(bucketWarning);
        }

        TimeWindow? window = startMSec is null && endMSec is null
            ? null
            : new TimeWindow(startMSec, endMSec);

        if (!TraceExecution.TryReadDualFormatReport(
            request.Path,
            "timeline",
            () => new TimelineProvider().Read(request.Path, window, lanes, buckets, scope),
            error,
            out TimelineResult? result))
        {
            return ExitCodes.InputError;
        }

        // Surface the process the scope resolved to (an explicit name or the automatic
        // busiest) so a narrowed machine-wide capture is not silently one process's view.
        if (result.Process is not null)
        {
            warnings.Add($"Scoped to process '{result.Process}'. Pass --all-processes to include every process.");
        }

        AnalysisResult<TimelineResult> envelope = new(result, warnings, SteeringHints.ForTimeline(result));

        if (request.Format == OutputFormat.Json)
        {
            output.WriteLine(OutputJson.Serialize(envelope));
        }
        else
        {
            TimelineTextRenderer.Render(envelope, request.Path, output);
        }

        return ExitCodes.Success;
    }
}
