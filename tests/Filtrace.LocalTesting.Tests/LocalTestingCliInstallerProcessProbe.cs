// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Runtime.CompilerServices;

namespace Filtrace.LocalTesting.Tests;

internal static class LocalTestingCliInstallerProcessProbe
{
    [ModuleInitializer]
    public static void Run()
    {
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
