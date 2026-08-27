// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  The observable result of one child CLI invocation.
/// </summary>
/// <param name="ExitCode">The child process exit code.</param>
/// <param name="StandardOutputLength">The captured standard-output character count.</param>
/// <param name="StandardErrorLength">The captured standard-error character count.</param>
public readonly record struct CliProcessResult(
    int ExitCode,
    int StandardOutputLength,
    int StandardErrorLength);
