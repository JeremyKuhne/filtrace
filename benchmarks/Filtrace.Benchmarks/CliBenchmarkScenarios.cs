// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

internal static class CliBenchmarkScenarios
{
    private static readonly CliScenarioDefinition[] Definitions =
    [
        new("info-warm", CliScenarioOperation.Info),
        new("rank-self-warm", CliScenarioOperation.RankSelf),
        new("rank-inclusive-warm", CliScenarioOperation.RankInclusive),
        new("rank-activity-warm", CliScenarioOperation.RankActivity),
        new("batch-8", CliScenarioOperation.Batch, CaseCount: 8),
        new("batch-24", CliScenarioOperation.Batch, CaseCount: 24),
        new("diff-8", CliScenarioOperation.Diff, CaseCount: 8),
        new("diff-24", CliScenarioOperation.Diff, CaseCount: 24),
        new("symbols-1", CliScenarioOperation.Symbols, SymbolDllCount: 1),
        new("symbols-32", CliScenarioOperation.Symbols, SymbolDllCount: 32),
        new("info-cold", CliScenarioOperation.Info, Cold: true),
        new("batch-cold-8", CliScenarioOperation.Batch, Cold: true, CaseCount: 8),
        new("batch-cold-24", CliScenarioOperation.Batch, Cold: true, CaseCount: 24),
        new("diff-cold-8", CliScenarioOperation.Diff, Cold: true, CaseCount: 8),
        new("diff-cold-24", CliScenarioOperation.Diff, Cold: true, CaseCount: 24)
    ];

    private static readonly IReadOnlyDictionary<string, CliScenarioDefinition> ByName =
        Definitions.ToDictionary(static definition => definition.Name, StringComparer.Ordinal);

    public static IEnumerable<string> WarmNames =>
        Definitions.Where(static definition => !definition.Cold).Select(static definition => definition.Name);

    public static IEnumerable<string> ColdManifestNames =>
        Definitions.Where(static definition => definition.Cold && definition.IsManifest)
            .Select(static definition => definition.Name);

    public static CliScenarioDefinition Get(string name) =>
        ByName.TryGetValue(name, out CliScenarioDefinition? definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown CLI scenario.");

    public static string[] CreateArguments(
        CliScenarioDefinition definition,
        string trace,
        string? beforeManifest = null,
        string? afterManifest = null,
        string? symbolsDirectory = null) =>
        definition.Operation switch
        {
            CliScenarioOperation.Info => ["info", trace, "--format", "json"],
            CliScenarioOperation.RankSelf =>
                ["rank", trace, "--metric", "cpu", "--format", "json"],
            CliScenarioOperation.RankInclusive =>
                ["rank", trace, "--metric", "cpu", "--measure", "inclusive", "--format", "json"],
            CliScenarioOperation.RankActivity =>
                ["rank", trace, "--metric", "cpu", "--activity", "Order", "--format", "json"],
            CliScenarioOperation.Batch when beforeManifest is not null =>
                ["batch", beforeManifest, "--format", "json"],
            CliScenarioOperation.Diff when beforeManifest is not null && afterManifest is not null =>
                ["diff", beforeManifest, afterManifest, "--format", "json"],
            CliScenarioOperation.Symbols when symbolsDirectory is not null =>
                ["info", trace, "--symbols", symbolsDirectory, "--format", "json"],
            _ => throw new ArgumentException(
                $"Scenario '{definition.Name}' is missing a required manifest or symbol directory.",
                nameof(definition))
        };
}
