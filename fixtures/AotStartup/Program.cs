// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Filtrace.Fixtures.AotStartup;

/// <summary>
///  A Native AOT capture target: a short-lived process whose CPU time lands in a few
///  named native frames under one named ancestor, optionally split across a parent and
///  a child so the two halves have to be told apart.
/// </summary>
/// <remarks>
///  <para>
///   Nothing here is interesting as a workload. What matters is that the frames come
///   from a product native image with its own PDB - not from the runtime, not from the
///   OS, and not from anything the public symbol server knows - so an analysis either
///   resolves them from the supplied local symbols or shows bare addresses.
///  </para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        int iterations = ReadIterations(args, defaultValue: 4000);

        if (Array.IndexOf(args, "--worker") >= 0)
        {
            return Report("worker", NativeEntryPoint.Execute(iterations));
        }

        if (Array.IndexOf(args, "--parent") >= 0)
        {
            return RunParent(iterations);
        }

        return Report("single", NativeEntryPoint.Execute(iterations));
    }

    // The shape the #62 investigation had to explain: a parent that does its own work,
    // launches a short-lived child, and then does more work after the child exits. The
    // parent's frames and the child's are disjoint on purpose, so a scope that confuses
    // them is obvious in the ranking rather than subtle.
    private static int RunParent(int iterations)
    {
        long total = HostStartup.Prepare(iterations);

        string? self = Environment.ProcessPath;
        if (self is null)
        {
            Console.Error.WriteLine("Cannot locate the running executable to launch a child.");
            return 1;
        }

        using Process child = Process.Start(new ProcessStartInfo(self)
        {
            Arguments = $"--worker --iterations {iterations.ToString(CultureInfo.InvariantCulture)}",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to launch the child process.");

        child.WaitForExit();
        total += HostStartup.Finish(iterations);

        Console.Out.WriteLine(
            $"parent pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} " +
            $"child pid {child.Id.ToString(CultureInfo.InvariantCulture)} " +
            $"exit {child.ExitCode.ToString(CultureInfo.InvariantCulture)} " +
            $"checksum {total.ToString(CultureInfo.InvariantCulture)}");
        return child.ExitCode;
    }

    private static int Report(string role, long checksum)
    {
        // Consuming the result keeps the compiler and the AOT optimizer from deleting the
        // work the capture is supposed to sample.
        Console.Out.WriteLine(
            $"{role} pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} " +
            $"checksum {checksum.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    private static int ReadIterations(string[] args, int defaultValue)
    {
        int index = Array.IndexOf(args, "--iterations");
        return index >= 0
            && index + 1 < args.Length
            && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0
                ? parsed
                : defaultValue;
    }
}

/// <summary>
///  The inclusive ancestor the command's own work hangs off, named after the frame the
///  #62 investigation needed in order to separate host startup from command code.
/// </summary>
internal static class NativeEntryPoint
{
    // Every method on the hot path is NoInlining: the fixture's value is the *shape* of
    // the stack, and an inlined leaf would erase the distinction between self time and
    // the ancestor's inclusive time that SC2 has to demonstrate.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long Execute(int iterations)
    {
        long total = 0;
        for (int i = 0; i < iterations; i++)
        {
            total += ComputeChecksum(i);
            total += TransformBuffer(i);
            total += SearchTable(i);
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ComputeChecksum(int seed)
    {
        long hash = 1469598103934665603;
        for (int i = 0; i < 512; i++)
        {
            hash ^= (byte)(seed + i);
            hash *= 1099511628211;
        }

        return hash & 0xFF;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long TransformBuffer(int seed)
    {
        Span<int> buffer = stackalloc int[128];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = seed + i;
        }

        long total = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (buffer[i] * 31) ^ (buffer[i] >> 3);
                total += buffer[i];
            }
        }

        return total & 0xFF;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long SearchTable(int seed)
    {
        long found = 0;
        for (int i = 0; i < 256; i++)
        {
            int probe = (int)((seed * 2654435761L) % 1024) + i;
            if ((probe & 0x3F) == (seed & 0x3F))
            {
                found++;
            }
        }

        return found;
    }
}

/// <summary>
///  The parent-only work: the frames that must not appear in a child-scoped ranking, and
///  must appear in a parent-only one.
/// </summary>
internal static class HostStartup
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long Prepare(int iterations) => Spin(iterations, salt: 7);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long Finish(int iterations) => Spin(iterations, salt: 13);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Spin(int iterations, int salt)
    {
        long total = 0;
        for (int i = 0; i < iterations; i++)
        {
            long value = (i * 2654435761L) ^ salt;
            for (int j = 0; j < 64; j++)
            {
                value = (value * 31) ^ (value >> 5);
                total += value;
            }
        }

        return total & 0xFF;
    }
}
