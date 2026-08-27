// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One CPU leaf method retained in a timeline snapshot.
/// </summary>
/// <param name="Name">Short method name.</param>
/// <param name="SampleCount">Samples attributed to the method.</param>
/// <param name="Percent">Percentage of all stack-bearing CPU samples in the window.</param>
public sealed record SnapshotCpuMethod(string Name, long SampleCount, double Percent);
