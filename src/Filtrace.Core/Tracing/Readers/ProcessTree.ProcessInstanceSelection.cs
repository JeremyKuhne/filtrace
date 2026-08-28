// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

internal static partial class ProcessTree
{
    /// <summary>
    ///  Contains the root and descendant process instances selected from a trace.
    /// </summary>
    internal sealed record ProcessInstanceSelection(
        HashSet<int> RootIndexes,
        HashSet<int> IncludedIndexes);
}