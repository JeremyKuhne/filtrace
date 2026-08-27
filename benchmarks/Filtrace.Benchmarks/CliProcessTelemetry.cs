// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal sealed record CliProcessTelemetry(
    int Iteration,
    IReadOnlyList<string> Arguments,
    double TotalProcessorMilliseconds,
    long PeakWorkingSetBytes,
    long MaxPrivateMemoryBytes,
    int ExitCode,
    int StandardOutputLength,
    int StandardErrorLength,
    string OutputSha256);
