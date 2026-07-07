// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>An allocation time bucket: <c>GCAllocationTick</c> count and the bytes attributed.</summary>
/// <param name="Count">Allocation-tick events in the bucket.</param>
/// <param name="Bytes">Bytes the ticks attribute to the bucket.</param>
public sealed record AllocBucket(long Count, long Bytes);
