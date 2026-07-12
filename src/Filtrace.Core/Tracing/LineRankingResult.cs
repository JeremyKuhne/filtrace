// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A line-level self-time ranking: leaf samples attributed to the source line
///  executing when each was taken, scoped to the methods matching a filter.
/// </summary>
/// <param name="ScopeWeight">Total scoped weight, in the metric's unit.</param>
/// <param name="MethodFilter">The substring the methods were matched on, or empty for every method.</param>
/// <param name="Rows">The ranked source lines, highest first.</param>
/// <param name="AttributedRecordCount">Matching records with a source location, or <see langword="null"/> when unavailable.</param>
/// <param name="UnattributedRecordCount">Matching records without a source location, or <see langword="null"/> when unavailable.</param>
public sealed record LineRankingResult(
	double ScopeWeight,
	string MethodFilter,
	IReadOnlyList<LineRow> Rows,
	int? AttributedRecordCount = null,
	int? UnattributedRecordCount = null);
