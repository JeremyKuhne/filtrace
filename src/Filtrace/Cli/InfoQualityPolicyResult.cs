// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The outcome of applying an <see cref="InfoQualityPolicy"/>.
/// </summary>
/// <param name="Failed">Whether at least one requested quality gate rejected the trace.</param>
/// <param name="Warnings">The trace evidence explaining each failed gate.</param>
internal sealed record InfoQualityPolicyResult(
    bool Failed,
    IReadOnlyList<string> Warnings);
