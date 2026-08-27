// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures manifest orchestration over preloaded independent case identities.
/// </summary>
[MemoryDiagnoser]
public class ManifestAnalyzerBenchmarks
{
    private CaptureManifest _after = null!;
    private CaptureManifest _before = null!;
    private LoadedTrace _trace = null!;

    /// <summary>
    ///  The number of manifest cases to analyze.
    /// </summary>
    [Params(1, 8, 24)]
    public int CaseCount { get; set; }

    /// <summary>
    ///  The future bounded concurrency contract; Phase 0 remains sequential.
    /// </summary>
    [Params(1, 2, 4, 8)]
    public int MaxDegreeOfParallelism { get; set; }

    /// <summary>
    ///  Builds manifests and one preloaded immutable trace outside measurement.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        StackSampleSource source = FoldingBenchmarkCorpus.Create("s10000-d20-f64");
        double totalWeight = source.Samples.Sum(static sample => sample.Weight);
        TraceInfo info = new(
            "preloaded.nettrace",
            TraceFormat.NetTrace,
            totalWeight,
            source.Samples.Count,
            symbolResolutionRate: 1.0,
            threads: [],
            warnings: [],
            availableAnalyses: ["cpu"]);
        _trace = new LoadedTrace(info, source);

        CaptureManifestCase[] beforeCases = new CaptureManifestCase[CaseCount];
        CaptureManifestCase[] afterCases = new CaptureManifestCase[CaseCount];
        for (int caseIndex = 0; caseIndex < CaseCount; caseIndex++)
        {
            string parameters = $"Case: {caseIndex}";
            beforeCases[caseIndex] = CreateCase($"before-{caseIndex}", parameters);
            afterCases[caseIndex] = CreateCase($"after-{caseIndex}", parameters);
        }

        _before = new CaptureManifest("before-manifest.json", null, beforeCases);
        _after = new CaptureManifest("after-manifest.json", null, afterCases);
    }

    /// <summary>
    ///  Runs one self-time query across every preloaded case.
    /// </summary>
    [Benchmark]
    public BatchRankingResult BatchSelf() =>
        CaptureManifestBatchAnalyzer.Analyze(
            _before,
            "cpu",
            inclusive: false,
            root: string.Empty,
            FrameNames.DefaultFoldPatterns,
            MaxDegreeOfParallelism,
            (_, _) => _trace);

    /// <summary>
    ///  Diffs self-time rankings across every preloaded case pair.
    /// </summary>
    [Benchmark]
    public CaptureManifestDiffAnalysis DiffSelf() =>
        CaptureManifestDiffAnalyzer.Analyze(
            _before,
            _after,
            inclusive: false,
            root: string.Empty,
            FrameNames.DefaultFoldPatterns,
            top: 5,
            MaxDegreeOfParallelism,
            (_, _) => _trace);

    private static CaptureManifestCase CreateCase(string id, string parameters) =>
        new(
            id,
            "Filtrace.Benchmarks.Aggregation",
            parameters,
            $"Aggregation({parameters})",
            $"{id}.nettrace",
            SymbolsDirectory: null,
            OperationCount: null,
            OperationUnit: null);
}
