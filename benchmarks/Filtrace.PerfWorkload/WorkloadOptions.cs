// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Filtrace.PerfWorkload;

/// <summary>
///  Defines the bounded inputs used to generate a Track D performance trace.
/// </summary>
/// <param name="Mode">The workload pattern to execute.</param>
/// <param name="Workers">The number of worker threads to start.</param>
/// <param name="DurationMilliseconds">The minimum duration of CPU work, in milliseconds.</param>
/// <param name="Depth">The recursive call depth used by each worker.</param>
/// <param name="ActivityRounds">The number of activity iterations to execute in activity mode.</param>
internal sealed record WorkloadOptions(
    WorkloadMode Mode,
    int Workers,
    int DurationMilliseconds,
    int Depth,
    int ActivityRounds)
{
    private const int MaximumWorkers = 256;
    private const int MaximumDurationMilliseconds = 600_000;
    private const int MaximumDepth = 128;
    private const int MaximumActivityRounds = 10_000_000;

    /// <summary>
    ///  Parses and validates the workload's command-line arguments.
    /// </summary>
    /// <param name="args">The command-line arguments, beginning with the workload mode.</param>
    /// <param name="options">The parsed options when all arguments are valid.</param>
    /// <param name="error">A usage or validation message when parsing fails.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(
        string[] args,
        [NotNullWhen(returnValue: true)] out WorkloadOptions? options,
        out string? error)
    {
        options = null;
        error = null;
        if (args.Length == 0)
        {
            error = Usage;
            return false;
        }

        WorkloadMode mode;
        if (string.Equals(args[0], "cpu", StringComparison.OrdinalIgnoreCase))
        {
            mode = WorkloadMode.Cpu;
        }
        else if (string.Equals(args[0], "activity", StringComparison.OrdinalIgnoreCase))
        {
            mode = WorkloadMode.Activity;
        }
        else
        {
            error = $"Unknown mode '{args[0]}'.{Environment.NewLine}{Usage}";
            return false;
        }

        int workers = Math.Min(Environment.ProcessorCount, 8);
        int durationMilliseconds = 15_000;
        int depth = 20;
        int activityRounds = 1_000;
        for (int index = 1; index < args.Length; index++)
        {
            string name = args[index];
            if (index + 1 >= args.Length)
            {
                error = $"Option '{name}' requires an integer value.";
                return false;
            }

            string value = args[++index];
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                error = $"Option '{name}' requires an integer value; got '{value}'.";
                return false;
            }

            switch (name)
            {
                case "--workers":
                    workers = parsed;
                    break;
                case "--duration-ms":
                    durationMilliseconds = parsed;
                    break;
                case "--depth":
                    depth = parsed;
                    break;
                case "--activity-rounds":
                    activityRounds = parsed;
                    break;
                default:
                    error = $"Unknown option '{name}'.{Environment.NewLine}{Usage}";
                    return false;
            }
        }

        if (workers is < 1 or > MaximumWorkers)
        {
            error = $"--workers must be in [1, {MaximumWorkers}].";
        }
        else if (durationMilliseconds is < 100 or > MaximumDurationMilliseconds)
        {
            error = $"--duration-ms must be in [100, {MaximumDurationMilliseconds}].";
        }
        else if (depth is < 1 or > MaximumDepth)
        {
            error = $"--depth must be in [1, {MaximumDepth}].";
        }
        else if (activityRounds is < 1 or > MaximumActivityRounds)
        {
            error = $"--activity-rounds must be in [1, {MaximumActivityRounds}].";
        }

        if (error is not null)
        {
            return false;
        }

        options = new WorkloadOptions(mode, workers, durationMilliseconds, depth, activityRounds);
        return true;
    }

    /// <summary>
    ///  Gets the command-line syntax accepted by the workload.
    /// </summary>
    public static string Usage =>
        "Usage: Filtrace.PerfWorkload <cpu|activity> "
            + "[--workers N] [--duration-ms N] [--depth N] [--activity-rounds N]";
}
