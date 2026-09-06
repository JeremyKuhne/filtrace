// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Filtrace.LocalTesting.Tests;

internal static class LocalTestingWrapperProcessProbe
{
    public const string ModeVariable = "FILTRACE_WRAPPER_PROBE_MODE";
    public const string ReadinessPathVariable = "FILTRACE_WRAPPER_PROBE_READINESS_PATH";
    public const string DotnetLogPathVariable = "FILTRACE_WRAPPER_PROBE_DOTNET_LOG_PATH";
    public const string GitLogPathVariable = "FILTRACE_WRAPPER_PROBE_GIT_LOG_PATH";
    public const string RealDotnetPathVariable = "FILTRACE_WRAPPER_PROBE_REAL_DOTNET_PATH";
    public const string HelperAssemblyPathVariable = "FILTRACE_WRAPPER_PROBE_HELPER_ASSEMBLY_PATH";
    public const string RepositoryRootVariable = "FILTRACE_WRAPPER_PROBE_REPOSITORY_ROOT";
    public const string GitDirectoryVariable = "FILTRACE_WRAPPER_PROBE_GIT_DIRECTORY";
    public const string ExitCodeVariable = "FILTRACE_WRAPPER_PROBE_EXIT_CODE";
    public const string StandardOutputVariable = "FILTRACE_WRAPPER_PROBE_STANDARD_OUTPUT";
    public const string StandardErrorVariable = "FILTRACE_WRAPPER_PROBE_STANDARD_ERROR";

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
            Console.Error.Write($"Wrapper probe readiness marker is missing: '{readinessPath}'.");
            Environment.Exit(125);
        }

        string executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
        string toolName;
        if (executableName.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            toolName = "git";
        }
        else if (executableName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            toolName = "dotnet";
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown wrapper probe executable '{executableName}'.");
        }

        RecordInvocation(toolName);
        if (toolName.Equals("dotnet", StringComparison.Ordinal)
            && mode.Equals("forward-helper", StringComparison.Ordinal))
        {
            Environment.Exit(ForwardToHelper());
        }

        if (toolName.Equals("git", StringComparison.Ordinal)
            && mode.Equals("forward-helper", StringComparison.Ordinal))
        {
            Console.Out.WriteLine(GetRequiredVariable(RepositoryRootVariable));
            Console.Out.WriteLine(GetRequiredVariable(GitDirectoryVariable));
        }
        else
        {
            Console.Out.Write(Environment.GetEnvironmentVariable(StandardOutputVariable));
        }

        Console.Error.Write(Environment.GetEnvironmentVariable(StandardErrorVariable));
        Environment.Exit(GetExitCode());
    }

    private static void RecordInvocation(string toolName)
    {
        string? logPath = Environment.GetEnvironmentVariable(
            toolName.Equals("git", StringComparison.Ordinal)
                ? GitLogPathVariable
                : DotnetLogPathVariable);

        if (string.IsNullOrEmpty(logPath))
        {
            return;
        }

        string[] arguments = Environment.GetCommandLineArgs();
        List<string> records = [$"TOOL={toolName}", $"CWD={Environment.CurrentDirectory}"];
        for (int index = 1; index < arguments.Length; index++)
        {
            records.Add($"ARG={arguments[index]}");
        }

        File.WriteAllLines(logPath, records);
    }

    private static int ForwardToHelper()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int separatorIndex = Array.IndexOf(arguments, "--");
        if (separatorIndex < 0)
        {
            throw new InvalidOperationException("The wrapper probe did not receive a helper separator.");
        }

        ProcessStartInfo startInfo = new(GetRequiredVariable(RealDotnetPathVariable))
        {
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(GetRequiredVariable(HelperAssemblyPathVariable));
        for (int index = separatorIndex + 1; index < arguments.Length; index++)
        {
            startInfo.ArgumentList.Add(arguments[index]);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the local-testing helper probe.");

        if (!process.WaitForExit((int)TimeSpan.FromSeconds(25).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
            return 124;
        }

        return process.ExitCode;
    }

    private static string GetRequiredVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Required wrapper probe variable '{name}' is missing.");
    }

    private static int GetExitCode()
    {
        string? value = Environment.GetEnvironmentVariable(ExitCodeVariable);
        return string.IsNullOrEmpty(value) ? 0 : int.Parse(value);
    }
}
