// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>A garbage-collection time bucket: collection count, summed and longest pause, and whether any was gen-2.</summary>
/// <param name="Count">Collections started in the bucket.</param>
/// <param name="TotalPauseMs">Summed managed-thread pause time, in milliseconds.</param>
/// <param name="MaxPauseMs">Longest single pause, in milliseconds.</param>
/// <param name="HasGen2">Whether any collection condemned generation 2.</param>
public sealed record GcBucket(int Count, double TotalPauseMs, double MaxPauseMs, bool HasGen2);
