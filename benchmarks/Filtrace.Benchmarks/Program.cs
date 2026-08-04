// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using BenchmarkDotNet.Running;

namespace Filtrace.Benchmarks;

public static class Program
{
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
