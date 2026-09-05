// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using BenchmarkDotNet.Running;

namespace Filtrace.Benchmarks;

/// <summary>
///  Dispatches either the private CLI telemetry campaign or the BenchmarkDotNet harness.
/// </summary>
public static class Program
{
    /// <summary>
    ///  Handles telemetry help and collection before forwarding ordinary filters and
    ///  configuration arguments to BenchmarkDotNet.
    /// </summary>
    /// <param name="args">The telemetry options or BenchmarkDotNet command line.</param>
    public static void Main(string[] args)
    {
        if (CliTelemetryCommand.IsHelpRequested(args))
        {
            Console.WriteLine(CliTelemetryCommand.Usage);
            return;
        }

        if (CliTelemetryCommand.IsRequested(args))
        {
            CliTelemetryCommand.RunAsync(args).GetAwaiter().GetResult();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
