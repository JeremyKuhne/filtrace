// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Filtrace.PerfWorkload;

/// <summary>
///  Runs bounded CPU or nested-activity work for Track D trace captures.
/// </summary>
public static class Program
{
    private const int OperationsPerBurst = 65_536;

    /// <summary>
    ///  Parses the workload mode and runs the requested worker set.
    /// </summary>
    /// <param name="args">Workload mode and bounded options.</param>
    /// <returns>Zero on success; two for invalid arguments.</returns>
    public static int Main(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(WorkloadOptions.Usage);
            return 0;
        }

        if (!WorkloadOptions.TryParse(args, out WorkloadOptions? options, out string? error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        long durationTicks = (long)Math.Ceiling(
            options.DurationMilliseconds * (double)Stopwatch.Frequency / 1000.0);

        long deadline = Stopwatch.GetTimestamp() + durationTicks;
        ulong[] checksums = new ulong[options.Workers];
        Thread[] workers = new Thread[options.Workers];
        for (int workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            int capturedIndex = workerIndex;
            workers[workerIndex] = new Thread(
                () => checksums[capturedIndex] = RunWorker(options, deadline, capturedIndex))
            {
                IsBackground = false,
                Name = $"TrackD worker {workerIndex}"
            };

            workers[workerIndex].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        ulong checksum = 0;
        foreach (ulong workerChecksum in checksums)
        {
            checksum ^= workerChecksum;
        }

        Console.WriteLine(
            $"mode={options.Mode.ToString().ToLowerInvariant()} workers={options.Workers} "
                + $"depth={options.Depth} checksum=0x{checksum:x16}");

        return 0;
    }

    private static ulong RunWorker(WorkloadOptions options, long deadline, int workerIndex) =>
        options.Mode == WorkloadMode.Activity
            ? RunActivities(options, deadline, workerIndex)
            : RunCpu(options, deadline, workerIndex);

    private static ulong RunCpu(WorkloadOptions options, long deadline, int workerIndex)
    {
        ulong state = (ulong)(workerIndex + 1);
        long burstCount = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            state = CpuBurst(options.Depth, state);
            burstCount++;
        }

        return state ^ (ulong)burstCount;
    }

    private static ulong RunActivities(WorkloadOptions options, long deadline, int workerIndex)
    {
        ulong state = (ulong)(workerIndex + 1);
        int round = 0;
        while (round < options.ActivityRounds && Stopwatch.GetTimestamp() < deadline)
        {
            TrackDActivitySource.Log.OrderStart();
            state = CpuBurst(options.Depth, state);

            TrackDActivitySource.Log.QueryStart();
            state = CpuBurst(options.Depth, state);
            TrackDActivitySource.Log.QueryStop();

            TrackDActivitySource.Log.RenderStart();
            state = CpuBurst(options.Depth, state);
            TrackDActivitySource.Log.RenderStop();

            state = CpuBurst(options.Depth, state);
            TrackDActivitySource.Log.OrderStop();
            round++;
        }

        return state ^ (uint)round;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong CpuBurst(int depth, ulong state)
    {
        for (int operation = 0; operation < OperationsPerBurst; operation++)
        {
            state = Work(depth, state + (uint)operation);
        }

        return state;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Work(int depth, ulong state)
    {
        state ^= 0x9E3779B97F4A7C15UL + (uint)depth;
        state = BitOperations.RotateLeft(state, 13) * 0xBF58476D1CE4E5B9UL;
        if (depth <= 1)
        {
            return state;
        }

        ulong nested = Work(depth - 1, state);
        return BitOperations.RotateLeft(nested ^ state, 17) * 0x94D049BB133111EBUL;
    }
}
