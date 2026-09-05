// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal static partial class ProcessTree
{
    /// <summary>
    ///  Describes one process-table instance and its parent relationship.
    /// </summary>
    /// <param name="Index">The trace-local process index that remains unique when an OS id is reused.</param>
    /// <param name="ProcessId">The operating-system process id.</param>
    /// <param name="Name">The process name, or <see langword="null"/> when the trace provides none.</param>
    /// <param name="ParentIndex">The trace-local parent index, or <see langword="null"/> for a root.</param>
    internal readonly record struct ProcessInstanceDescriptor(
        int Index,
        int ProcessId,
        string? Name,
        int? ParentIndex);
}
