// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Defines the versioned, reproducible campaign metadata and its per-launch observations.
/// </summary>
/// <param name="SchemaVersion">The report contract version used by downstream comparisons.</param>
/// <param name="CreatedUtc">The round-trip UTC timestamp recorded after collection.</param>
/// <param name="Scenario">The registered scenario key that was executed.</param>
/// <param name="Iterations">The requested launch count.</param>
/// <param name="Executable">The full path of the measured filtrace executable.</param>
/// <param name="Complete">Whether every requested launch produced a valid complete observation.</param>
/// <param name="ChildWallP50Milliseconds">The nearest-rank median of individual child wall durations.</param>
/// <param name="ChildWallP95Milliseconds">The nearest-rank 95th percentile of individual child wall durations.</param>
/// <param name="Failure">The collection failure when the report is incomplete.</param>
/// <param name="Launches">The ordered observations, one for each campaign iteration.</param>
internal sealed record CliTelemetryReport(
    int SchemaVersion,
    string CreatedUtc,
    string Scenario,
    int Iterations,
    string Executable,
    bool Complete,
    double? ChildWallP50Milliseconds,
    double? ChildWallP95Milliseconds,
    string? Failure,
    IReadOnlyList<CliProcessTelemetry> Launches)
{
    /// <summary>
    ///  Gets the current serialized telemetry schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    ///  Creates a report and emits child-wall percentiles only for a complete,
    ///  valid launch set.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Percentiles use nearest rank: sort individual launch durations and select
    ///   item <c>ceil(p * n)</c>, using one-based ranks.
    ///  </para>
    /// </remarks>
    /// <param name="createdUtc">The round-trip UTC timestamp recorded after collection.</param>
    /// <param name="scenario">The registered scenario key that was executed.</param>
    /// <param name="iterations">The requested launch count.</param>
    /// <param name="executable">The full path of the measured executable.</param>
    /// <param name="launches">The ordered successful observations collected before completion or failure.</param>
    /// <param name="failure">
    ///  The collection failure, or <see langword="null"/> when collection returned normally.
    /// </param>
    /// <returns>A complete report with percentiles, or an explicitly incomplete report without them.</returns>
    public static CliTelemetryReport Create(
        string createdUtc,
        string scenario,
        int iterations,
        string executable,
        IReadOnlyList<CliProcessTelemetry> launches,
        string? failure)
    {
        bool complete = failure is null && HasValidLaunchSet(launches, iterations);
        double? childWallP50Milliseconds = null;
        double? childWallP95Milliseconds = null;
        if (complete)
        {
            double[] elapsed = [.. launches
                .Select(static launch => launch.ElapsedMilliseconds!.Value)
                .Order()];

            childWallP50Milliseconds = CalculateNearestRank(elapsed, 0.50);
            childWallP95Milliseconds = CalculateNearestRank(elapsed, 0.95);
        }

        return new CliTelemetryReport(
            CurrentSchemaVersion,
            createdUtc,
            scenario,
            iterations,
            executable,
            complete,
            childWallP50Milliseconds,
            childWallP95Milliseconds,
            complete ? null : failure ?? "Telemetry observations were incomplete or invalid.",
            launches);
    }

    /// <summary>
    ///  Validates that a deserialized report is complete and internally consistent.
    /// </summary>
    /// <param name="expectedIterations">The launch count required by the caller.</param>
    /// <returns><see langword="true"/> when the report is valid for comparison.</returns>
    public bool HasValidCompleteLaunchSet(int expectedIterations)
    {
        if (SchemaVersion != CurrentSchemaVersion
            || !Complete
            || Iterations != expectedIterations
            || Failure is not null
            || ChildWallP50Milliseconds is not double p50
            || ChildWallP95Milliseconds is not double p95
            || !double.IsFinite(p50)
            || !double.IsFinite(p95)
            || p50 <= 0
            || p95 <= 0
            || !HasValidLaunchSet(Launches, expectedIterations))
        {
            return false;
        }

        double[] elapsed = [.. Launches
            .Select(static launch => launch.ElapsedMilliseconds!.Value)
            .Order()];

        return p50 == CalculateNearestRank(elapsed, 0.50)
            && p95 == CalculateNearestRank(elapsed, 0.95);
    }

    private static bool HasValidLaunchSet(
        IReadOnlyList<CliProcessTelemetry>? launches,
        int expectedIterations)
    {
        if (expectedIterations <= 0 || launches is null || launches.Count != expectedIterations)
        {
            return false;
        }

        string? outputSha256 = null;
        for (int index = 0; index < launches.Count; index++)
        {
            CliProcessTelemetry? launch = launches[index];
            if (launch is null
                || launch.Iteration != index + 1
                || launch.Arguments is null
                || launch.Arguments.Count == 0
                || launch.ElapsedMilliseconds is not double elapsed
                || !double.IsFinite(elapsed)
                || elapsed <= 0
                || !double.IsFinite(launch.TotalProcessorMilliseconds)
                || launch.TotalProcessorMilliseconds < 0
                || launch.PeakWorkingSetBytes < 0
                || launch.MaxPrivateMemoryBytes < 0
                || launch.ExitCode != 0
                || launch.StandardOutputLength <= 0
                || launch.StandardErrorLength != 0
                || string.IsNullOrEmpty(launch.OutputSha256)
                || launch.OutputSha256.Length != 64
                || launch.OutputSha256.Any(static character => !Uri.IsHexDigit(character)))
            {
                return false;
            }

            if (outputSha256 is null)
            {
                outputSha256 = launch.OutputSha256;
            }
            else if (!string.Equals(outputSha256, launch.OutputSha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static double CalculateNearestRank(double[] sortedValues, double percentile)
    {
        int rank = (int)Math.Ceiling(percentile * sortedValues.Length);
        return sortedValues[rank - 1];
    }
}
