// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal sealed record CliScenarioDefinition(
    string Name,
    CliScenarioOperation Operation,
    bool Cold = false,
    int CaseCount = 0,
    int SymbolDllCount = 0)
{
    public bool IsManifest => Operation is CliScenarioOperation.Batch or CliScenarioOperation.Diff;

    public bool IsPaired => Operation == CliScenarioOperation.Diff;
}
