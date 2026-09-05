// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One named count retained in a timeline snapshot.
/// </summary>
/// <param name="Name">The exception type or method name.</param>
/// <param name="Count">Occurrences in the snapshot window.</param>
public sealed record SnapshotCountRow(string Name, long Count);
