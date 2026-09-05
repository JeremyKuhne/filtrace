// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Describes one bounded child-process invocation.
/// </summary>
/// <param name="FileName">The executable path or name.</param>
/// <param name="Arguments">The structured argument list.</param>
/// <param name="WorkingDirectory">The child process working directory.</param>
/// <param name="Timeout">The maximum permitted execution time.</param>
/// <param name="EnvironmentVariables">Environment values to set, or names to remove when the value is null.</param>
internal sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null);