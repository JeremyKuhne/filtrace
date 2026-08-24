// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  How much of the already-applied process, activity, and time scope survived a
///  stack-ancestry root filter.
/// </summary>
/// <param name="AvailableWeight">
///  Metric weight before the root filter, including records whose stack is empty
///  and therefore cannot prove the selected ancestry.
/// </param>
/// <param name="RetainedWeight">Metric weight whose stack contains the selected root.</param>
/// <param name="RetainedPercent">Percentage of available weight retained by the root.</param>
/// <param name="AvailableRecordCount">
///  Records before the root filter, including empty stacks, or <see langword="null"/>
///  when record counts have no defined meaning for the source.
/// </param>
/// <param name="RetainedRecordCount">
///  Records surviving the root filter, or <see langword="null"/> when record counts
///  have no defined meaning for the source.
/// </param>
public sealed record RootScopeCoverage(
    double AvailableWeight,
    double RetainedWeight,
    double RetainedPercent,
    int? AvailableRecordCount,
    int? RetainedRecordCount);