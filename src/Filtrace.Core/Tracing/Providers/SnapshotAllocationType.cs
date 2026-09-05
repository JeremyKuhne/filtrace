// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One allocation type retained in a timeline snapshot.
/// </summary>
/// <param name="Name">Allocated type name.</param>
/// <param name="TickCount">Allocation ticks for the type.</param>
/// <param name="Bytes">Sampled allocation bytes for the type.</param>
public sealed record SnapshotAllocationType(string Name, long TickCount, long Bytes);
