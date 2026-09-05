// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Runtime.CompilerServices;

namespace Filtrace.LocalTesting.Tests;

internal static class LocalTestingCliInstallerProcessProbe
{
    public const string EnvironmentOutputVariable =
        "FILTRACE_LOCAL_TESTING_CLI_INSTALLER_ENVIRONMENT_OUTPUT";

    public const string FailureVariable = "FILTRACE_LOCAL_TESTING_CLI_INSTALLER_FAILURE_PROBE";

    [ModuleInitializer]
    public static void Run()
    {
        string? environmentOutput = Environment.GetEnvironmentVariable(EnvironmentOutputVariable);
        if (!string.IsNullOrEmpty(environmentOutput))
        {
            File.WriteAllText(
                environmentOutput,
                Environment.GetEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH") ?? "<null>");

            Environment.Exit(9);
        }

        if ("1".Equals(
            Environment.GetEnvironmentVariable(FailureVariable),
            StringComparison.Ordinal))
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int toolPathIndex = Array.IndexOf(arguments, "--tool-path");
            string toolPath = arguments[toolPathIndex + 1];
            Directory.CreateDirectory(toolPath);
            File.WriteAllText(Path.Join(toolPath, "partial-install"), string.Empty);
            Environment.Exit(9);
        }

        if (!"1".Equals(
            Environment.GetEnvironmentVariable(
                "FILTRACE_LOCAL_TESTING_CLI_INSTALLER_TIMEOUT_PROBE"),
            StringComparison.Ordinal))
        {
            return;
        }

        Thread.Sleep(TimeSpan.FromSeconds(30));
        Environment.Exit(0);
    }
}
