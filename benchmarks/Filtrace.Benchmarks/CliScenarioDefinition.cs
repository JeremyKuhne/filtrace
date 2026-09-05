// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Defines one reproducible command shape and the corpus preparation it requires.
/// </summary>
/// <param name="Name">The stable key accepted by benchmark and telemetry selectors.</param>
/// <param name="Operation">The filtrace command shape to invoke.</param>
/// <param name="Cold">Whether each launch requires a new trace identity with no ETLX cache.</param>
/// <param name="CaseCount">The cases per manifest arm, or zero for a non-manifest scenario.</param>
/// <param name="SymbolDllCount">The controlled symbol-directory size, or zero when symbols are not varied.</param>
internal sealed record CliScenarioDefinition(
    string Name,
    CliScenarioOperation Operation,
    bool Cold = false,
    int CaseCount = 0,
    int SymbolDllCount = 0)
{
    /// <summary>
    ///  Whether preparation requires a generated capture manifest.
    /// </summary>
    public bool IsManifest => Operation is CliScenarioOperation.Batch or CliScenarioOperation.Diff;

    /// <summary>
    ///  Whether preparation requires matching baseline and current manifest arms.
    /// </summary>
    public bool IsPaired => Operation == CliScenarioOperation.Diff;
}
