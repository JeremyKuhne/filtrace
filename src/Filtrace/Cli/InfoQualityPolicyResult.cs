// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Cli;

/// <summary>
///  The outcome of applying an <see cref="InfoQualityPolicy"/>.
/// </summary>
internal sealed record InfoQualityPolicyResult(
    bool Failed,
    IReadOnlyList<string> Warnings);
