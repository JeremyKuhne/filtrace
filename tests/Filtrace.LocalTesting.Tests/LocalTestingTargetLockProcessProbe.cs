// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Runtime.CompilerServices;

namespace Filtrace.LocalTesting.Tests;

internal static class LocalTestingTargetLockProcessProbe
{
    public const string EnabledVariable = "FILTRACE_LOCAL_TESTING_TEST_LOCK_PROBE";
    public const string TargetRootVariable = "FILTRACE_LOCK_PROBE_TARGET_ROOT";
    public const string GitDirectoryVariable = "FILTRACE_LOCK_PROBE_GIT_DIRECTORY";
    public const string ReadyPathVariable = "FILTRACE_LOCK_PROBE_READY_PATH";
    public const string ReleasePathVariable = "FILTRACE_LOCK_PROBE_RELEASE_PATH";

    [ModuleInitializer]
    public static void Run()
    {
        if (!"1".Equals(
            Environment.GetEnvironmentVariable(EnabledVariable),
            StringComparison.Ordinal))
        {
            return;
        }

        string? targetRoot = Environment.GetEnvironmentVariable(TargetRootVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        string gitDirectory = ReadRequiredVariable(GitDirectoryVariable);
        string readyPath = ReadRequiredVariable(ReadyPathVariable);
        string releasePath = ReadRequiredVariable(ReleasePathVariable);
        using LocalTestingTargetLock targetLock = LocalTestingTargetLock.Acquire(
            ResourcePlan.Create(targetRoot, gitDirectory));
        File.WriteAllText(readyPath, string.Empty);
        if (!SpinWait.SpinUntil(() => File.Exists(releasePath), TimeSpan.FromSeconds(15)))
        {
            Environment.Exit(2);
        }

        Environment.Exit(0);
    }

    private static string ReadRequiredVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Missing environment variable '{name}'.");
    }
}