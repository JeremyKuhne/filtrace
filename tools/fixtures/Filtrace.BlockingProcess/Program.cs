// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Text.Json;

namespace Filtrace.BlockingProcess;

/// <summary>
///  Waits for a parent-controlled release signal while consuming negligible CPU.
/// </summary>
internal static class Program
{
    private const string ReadyPathVariable = "FILTRACE_ELAPSED_READY_PATH";
    private const string ReleasePathVariable = "FILTRACE_ELAPSED_RELEASE_PATH";
    private const string ArgumentsPathVariable = "FILTRACE_ELAPSED_ARGUMENTS_PATH";
    private static readonly TimeSpan s_selfDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    ///  Publishes readiness, waits for release, and writes bounded successful output.
    /// </summary>
    /// <param name="args">Arguments to retain when the parent requests argv evidence.</param>
    public static void Main(string[] args)
    {
        string readyPath = GetRequiredPath(ReadyPathVariable);
        string releasePath = GetRequiredPath(ReleasePathVariable);
        string? argumentsPath = Environment.GetEnvironmentVariable(ArgumentsPathVariable);
        if (!string.IsNullOrEmpty(argumentsPath))
        {
            File.WriteAllText(argumentsPath, JsonSerializer.Serialize(args));
        }

        File.WriteAllText(readyPath, string.Empty);
        Stopwatch wait = Stopwatch.StartNew();
        while (!File.Exists(releasePath))
        {
            if (wait.Elapsed >= s_selfDeadline)
            {
                throw new TimeoutException("The parent did not publish the release signal within 30 seconds.");
            }

            Thread.Sleep(10);
        }

        Console.WriteLine("{\"status\":\"ok\"}");
    }

    private static string GetRequiredPath(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(path)
            ? throw new InvalidOperationException($"Environment variable '{variable}' is required.")
            : path;
    }
}