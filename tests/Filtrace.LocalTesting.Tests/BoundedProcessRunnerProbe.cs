// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Filtrace.LocalTesting.Tests;

internal static class BoundedProcessRunnerProbe
{
    public const string ModeVariable = "FILTRACE_BOUNDED_RUNNER_PROBE_MODE";
    public const string ChildProcessIdPathVariable = "FILTRACE_BOUNDED_RUNNER_CHILD_PID_PATH";
    public const string ChildReleasePathVariable = "FILTRACE_BOUNDED_RUNNER_CHILD_RELEASE_PATH";
    public const string EnvironmentValueVariable = "FILTRACE_BOUNDED_RUNNER_ENVIRONMENT_VALUE";

    [ModuleInitializer]
    public static void Run()
    {
        string? mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (mode is null)
        {
            return;
        }

        if (mode.Equals("inherited-pipe-parent", StringComparison.Ordinal))
        {
            ProcessStartInfo startInfo = new(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add(typeof(BoundedProcessRunnerProbe).Assembly.Location);
            startInfo.Environment[ModeVariable] = "inherited-pipe-child";
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start inherited-pipe probe child.");

            Environment.Exit(0);
        }

        if (mode.Equals("execution-timeout-tree", StringComparison.Ordinal))
        {
            ProcessStartInfo startInfo = new(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add(typeof(BoundedProcessRunnerProbe).Assembly.Location);
            startInfo.Environment[ModeVariable] = "execution-timeout-tree-child";
            using Process childProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start timeout probe child.");

            string processIdPath = Environment.GetEnvironmentVariable(ChildProcessIdPathVariable)
                ?? throw new InvalidOperationException("The child PID path is required.");

            PublishProcessId(processIdPath, childProcess.Id);
            Console.Out.Write("started");
            Thread.Sleep(TimeSpan.FromSeconds(30));
            Environment.Exit(0);
        }

        if (mode.Equals("execution-timeout-tree-child", StringComparison.Ordinal))
        {
            string releasePath = Environment.GetEnvironmentVariable(ChildReleasePathVariable)
                ?? throw new InvalidOperationException("The child release path is required.");

            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(releasePath) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(20));
            }

            Environment.Exit(0);
        }

        if (mode.Equals("inherited-pipe-child", StringComparison.Ordinal))
        {
            string processIdPath = Environment.GetEnvironmentVariable(ChildProcessIdPathVariable)
                ?? throw new InvalidOperationException("The child PID path is required.");

            string releasePath = Environment.GetEnvironmentVariable(ChildReleasePathVariable)
                ?? throw new InvalidOperationException("The child release path is required.");

            PublishProcessId(processIdPath, Environment.ProcessId);
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(releasePath) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(20));
            }

            Environment.Exit(0);
        }

        if (mode.Equals("arguments-environment", StringComparison.Ordinal))
        {
            string[] arguments = Environment.GetCommandLineArgs();
            Console.Out.WriteLine(arguments[1]);
            Console.Out.Write(arguments[2]);
            Console.Error.Write(Environment.GetEnvironmentVariable(EnvironmentValueVariable) ?? "<missing>");
            Environment.Exit(0);
        }

        if (mode.Equals("execution-timeout", StringComparison.Ordinal))
        {
            Console.Out.Write("started");
            Thread.Sleep(TimeSpan.FromSeconds(30));
            Environment.Exit(0);
        }

        if (mode.Equals("oversized-output", StringComparison.Ordinal))
        {
            string standardOutput = new('O', BoundedProcessRunner.MaxOutputCharacters + 4096);
            string standardError = new('E', BoundedProcessRunner.MaxOutputCharacters + 4096);
            Task output = Task.Run(() => Console.Out.Write(standardOutput));
            Task error = Task.Run(() => Console.Error.Write(standardError));
            Task.WaitAll(output, error);
            Environment.Exit(9);
        }

        throw new InvalidOperationException($"Unknown bounded-runner probe mode '{mode}'.");
    }

    private static void PublishProcessId(string path, int processId)
    {
        string temporaryPath = $"{path}.tmp";
        File.WriteAllText(temporaryPath, processId.ToString());
        File.Move(temporaryPath, path);
    }
}