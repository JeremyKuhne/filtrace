// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal static partial class ProcessTree
{
    /// <summary>
    ///  Describes one process-table instance and its parent relationship.
    /// </summary>
    internal readonly record struct ProcessInstanceDescriptor(
        int Index,
        int ProcessId,
        string? Name,
        int? ParentIndex);
}