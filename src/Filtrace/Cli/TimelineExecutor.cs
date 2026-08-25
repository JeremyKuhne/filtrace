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
        if (!Enum.IsDefined(request.Mode))
        {
            error.WriteLine($"Unknown timeline mode '{request.Mode}'. Valid modes: buckets, snapshot.");
            return ExitCodes.UsageError;
        }

        if (!RankRequestFactory.TryResolveScope(
            request.Process, request.ProcessIds, request.Children, request.AllProcesses, out ScopeRequest scope, out string? scopeError))
        {
            error.WriteLine(scopeError);
            return ExitCodes.UsageError;
        }

        List<string> warnings = [];
        TimelineResult? result;
        if (request.Mode == TimelineMode.Snapshot)
        {
            if (!TryValidateSnapshot(request, error, out double atMs))
            {
                return ExitCodes.UsageError;
            }

            if (!TraceExecution.TryReadDualFormatReport(
                request.Path,
                "timeline snapshot",
                () => new TimelineProvider().ReadSnapshot(request.Path, atMs, request.SnapshotHalfWindowMs, scope),
                error,
                out result))
            {
                return ExitCodes.InputError;
            }
        }
        else
        {
            if (request.AtMs is not null || request.SnapshotHalfWindowMs != TimelineProvider.DefaultSnapshotHalfWindowMs)
            {
                error.WriteLine("--at and --window require --mode snapshot.");
                return ExitCodes.UsageError;
            }

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
                out result))
            {
                return ExitCodes.InputError;
            }
        }

        // Surface the process the scope resolved to (an explicit name or the automatic
        // busiest) so a narrowed machine-wide capture is not silently one process's view.
        if (result.Process is not null)
        {
            warnings.Add($"Scoped to process '{result.Process}'. Pass --all-processes to include every process.");
        }

        if (result.Snapshot?.NamesTruncated == true)
        {
            warnings.Add($"Snapshot names longer than {TimelineProvider.MaxSnapshotNameChars} characters were truncated.");
        }

        if (TimelineProvider.GetSnapshotDetailWarning(result) is string detailWarning)
        {
            warnings.Add(detailWarning);
        }

        AnalysisResult<TimelineResult> envelope = new(
            result,
            warnings,
            SteeringHints.ForTimeline(result),
            new AnalysisContext("timeline"));

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

    private static bool TryValidateSnapshot(TimelineRequest request, TextWriter error, out double atMs)
    {
        if (request.AtMs is not double center)
        {
            error.WriteLine("--at is required when --mode snapshot is selected.");
            atMs = 0.0;
            return false;
        }

        if (!double.IsFinite(center) || center < 0.0)
        {
            error.WriteLine("--at must be a finite, non-negative timestamp in milliseconds.");
            atMs = 0.0;
            return false;
        }

        if (!double.IsFinite(request.SnapshotHalfWindowMs)
            || request.SnapshotHalfWindowMs < TimelineProvider.MinSnapshotHalfWindowMs
            || request.SnapshotHalfWindowMs > TimelineProvider.MaxSnapshotHalfWindowMs)
        {
            error.WriteLine(
                $"--window must be finite and from {TimelineProvider.MinSnapshotHalfWindowMs:N2} "
                + $"through {TimelineProvider.MaxSnapshotHalfWindowMs:N0} ms.");
            atMs = 0.0;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Time)
            || !string.IsNullOrWhiteSpace(request.Lanes)
            || request.BucketCount != TimelineProvider.DefaultBucketCount)
        {
            error.WriteLine("--time, --lanes, and --buckets apply only to --mode buckets.");
            atMs = 0.0;
            return false;
        }

        atMs = center;
        return true;
    }
}
