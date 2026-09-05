// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  One raw event type retained in a timeline snapshot.
/// </summary>
/// <param name="Provider">Event provider name.</param>
/// <param name="Name">Event name.</param>
/// <param name="Count">Occurrences in the snapshot window.</param>
public sealed record SnapshotEventType(string Provider, string Name, long Count);
