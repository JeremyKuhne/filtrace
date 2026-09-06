// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Filtrace.LocalTesting.Tests;

internal static class CommandCaptureProcessProbe
{
    public const string ModeVariable = "FILTRACE_COMMAND_CAPTURE_PROBE_MODE";
    public const string ReadinessPathVariable = "FILTRACE_COMMAND_CAPTURE_PROBE_READINESS_PATH";
    public const string RecordDirectoryVariable = "FILTRACE_COMMAND_CAPTURE_RECORD_DIRECTORY";
    public const string WorkloadRecordPathVariable = "FILTRACE_COMMAND_CAPTURE_WORKLOAD_RECORD_PATH";
    public const string HostEditionVariable = "FILTRACE_COMMAND_CAPTURE_HOST_EDITION";
    public const string HostVersionVariable = "FILTRACE_COMMAND_CAPTURE_HOST_VERSION";

    private const string CollectorMode = "collector";
    private const string WorkloadMode = "workload";

    [ModuleInitializer]
    public static void Run()
    {
        string? mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (mode is null)
        {
            return;
        }

        string readinessPath = GetRequiredVariable(ReadinessPathVariable);
        if (!File.Exists(readinessPath))
        {
            Console.Error.Write($"Command-capture probe readiness marker is missing: '{readinessPath}'.");
            Environment.Exit(125);
        }

        string executableName = Path.GetFileName(Environment.ProcessPath) ?? string.Empty;
        if (mode.Equals(CollectorMode, StringComparison.Ordinal))
        {
            RequireExecutableName(executableName, "fakefiltrace.exe");
            RunCollector();
        }

        if (mode.Equals(WorkloadMode, StringComparison.Ordinal))
        {
            RequireExecutableName(executableName, "nativeargvrecorder.exe");
            RunWorkloadRecorder();
        }

        throw new InvalidOperationException($"Unknown command-capture probe mode '{mode}'.");
    }

    private static void RunCollector()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Length == 2 && arguments[1].Equals("--version", StringComparison.Ordinal))
        {
            Console.Out.Write("1.2.3-native-contract");
            Environment.Exit(0);
        }

        if (arguments.Length == 3
            && arguments[1].Equals("collect", StringComparison.Ordinal)
            && arguments[2].Equals("--help", StringComparison.Ordinal))
        {
            Console.Out.Write("collect --iterations --format --launch --launch-args --output");
            Environment.Exit(0);
        }

        if (arguments.Length < 2 || !arguments[1].Equals("collect", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The native fake collector received an unknown command.");
        }

        int outputIndex = RequireOption(arguments, "--output");
        int iterationsIndex = RequireOption(arguments, "--iterations");
        int launchIndex = RequireOption(arguments, "--launch");
        int launchArgumentsIndex = Array.IndexOf(arguments, "--launch-args");
        string tracePath = arguments[outputIndex + 1];
        string launchPath = arguments[launchIndex + 1];
        string launchArguments = string.Empty;
        if (launchArgumentsIndex >= 0)
        {
            launchArguments = GetOptionValue(arguments, launchArgumentsIndex, "--launch-args");
        }

        int iterations = int.Parse(arguments[iterationsIndex + 1]);
        if (iterations != 1)
        {
            throw new InvalidOperationException("The native fake collector requires exactly one iteration.");
        }

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        ProcessStartInfo startInfo = new(launchPath)
        {
            Arguments = launchArguments,
            UseShellExecute = false
        };

        startInfo.Environment[ModeVariable] = WorkloadMode;
        string recordDirectory = GetRequiredVariable(RecordDirectoryVariable);
        string caseId = Path.GetFileNameWithoutExtension(tracePath);
        string workloadRecordPath = Path.Join(recordDirectory, $"{caseId}.workload.json");
        startInfo.Environment[WorkloadRecordPathVariable] = workloadRecordPath;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the native argv recorder.");

        if (!process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
            Console.Error.Write("The native argv recorder exceeded its deadline.");
            Environment.Exit(124);
        }

        DateTimeOffset stoppedUtc = DateTimeOffset.UtcNow;
        if (process.ExitCode != 0)
        {
            Console.Error.Write($"The native argv recorder exited with code {process.ExitCode}.");
            Environment.Exit(process.ExitCode);
        }

        CollectorRecord collectorRecord = new()
        {
            Arguments = arguments[1..],
            Launch = launchPath,
            LaunchArguments = launchArguments
        };

        WriteJson(Path.Join(recordDirectory, $"{caseId}.collector.json"), collectorRecord);
        File.WriteAllBytes(tracePath, [0x46, 0x54, 0x50, 0x52, 0x4f, 0x42, 0x45]);

        CollectResult result = new()
        {
            Result = new CollectResultBody
            {
                CpuSample = new CpuSampleResult
                {
                    EffectiveMSec = 1.0,
                    Clamped = false
                },
                Invocations =
                [
                    new InvocationResult
                    {
                        Ordinal = 1,
                        ProcessId = process.Id,
                        ExitCode = process.ExitCode,
                        StartedUtc = startedUtc,
                        StoppedUtc = stoppedUtc
                    },
                ]
            }
        };

        Console.Out.Write(JsonSerializer.Serialize(result));
        Environment.Exit(0);
    }

    private static void RunWorkloadRecorder()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        WorkloadRecord record = new()
        {
            Arguments = arguments[1..],
            ProcessPath = Environment.ProcessPath ?? string.Empty,
            ProcessId = Environment.ProcessId,
            HostEdition = GetRequiredVariable(HostEditionVariable),
            HostVersion = GetRequiredVariable(HostVersionVariable)
        };

        WriteJson(GetRequiredVariable(WorkloadRecordPathVariable), record);
        Environment.Exit(0);
    }

    private static int RequireOption(string[] arguments, string option)
    {
        int index = Array.IndexOf(arguments, option);
        _ = GetOptionValue(arguments, index, option);
        return index;
    }

    private static string GetOptionValue(string[] arguments, int index, string option)
    {
        if (index < 1 || index >= arguments.Length - 1)
        {
            throw new InvalidOperationException($"The native fake collector did not receive a value for '{option}'.");
        }

        return arguments[index + 1];
    }

    private static void RequireExecutableName(string actualName, string expectedName)
    {
        if (!actualName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Command-capture probe mode expected '{expectedName}', not '{actualName}'.");
        }
    }

    private static string GetRequiredVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Required command-capture probe variable '{name}' is missing.");
    }

    private static void WriteJson<T>(string path, T value)
    {
        string json = JsonSerializer.Serialize(value);
        File.WriteAllText(path, json);
    }

    private sealed class CollectorRecord
    {
        public required string[] Arguments { get; init; }
        public required string Launch { get; init; }
        public required string LaunchArguments { get; init; }
    }

    private sealed class WorkloadRecord
    {
        public required string[] Arguments { get; init; }
        public required string ProcessPath { get; init; }
        public required int ProcessId { get; init; }
        public required string HostEdition { get; init; }
        public required string HostVersion { get; init; }
    }

    private sealed class CollectResult
    {
        public required CollectResultBody Result { get; init; }
    }

    private sealed class CollectResultBody
    {
        public required CpuSampleResult CpuSample { get; init; }
        public required InvocationResult[] Invocations { get; init; }
    }

    private sealed class CpuSampleResult
    {
        public required double EffectiveMSec { get; init; }
        public required bool Clamped { get; init; }
    }

    private sealed class InvocationResult
    {
        public required int Ordinal { get; init; }
        public required int ProcessId { get; init; }
        public required int ExitCode { get; init; }
        public required DateTimeOffset StartedUtc { get; init; }
        public required DateTimeOffset StoppedUtc { get; init; }
    }
}
