// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Holds a child process's separate bounded output streams and exit status.
/// </summary>
/// <param name="ExitCode">The exit code, or null when termination could not be confirmed.</param>
/// <param name="StandardOutput">The bounded standard output text.</param>
/// <param name="StandardError">The bounded standard error text.</param>
/// <param name="StandardOutputTruncated">Whether standard output exceeded its limit.</param>
/// <param name="StandardErrorTruncated">Whether standard error exceeded its limit.</param>
/// <param name="ExecutionTimedOut">Whether the root-completion wait expired, even if an exit code was later observed.</param>
/// <param name="OutputCaptureIncomplete">
///  Whether both redirected streams did not complete at end-of-file within the drain deadline,
///  or a read failed or was canceled.
/// </param>
/// <param name="RootProcessId">The root process identifier, when a real process was started.</param>
internal sealed record ProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    bool ExecutionTimedOut,
    bool OutputCaptureIncomplete = false,
    int? RootProcessId = null);