// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Runs child processes used to prepare local-testing artifacts.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    ///  Runs one process and captures its output without merging streams.
    /// </summary>
    /// <param name="invocation">The structured process request.</param>
    /// <returns>The separate bounded streams and exit status.</returns>
    Task<ProcessResult> RunAsync(ProcessInvocation invocation);
}