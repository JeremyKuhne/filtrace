// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures batch and diff over fresh manifest trees with no ETLX caches.
/// </summary>
[MemoryDiagnoser]
public class CliColdManifestBenchmarks
{
    private string[] _arguments = null!;
    private int _caseCount;
    private string _executable = null!;
    private CliManifestCorpus? _manifestCorpus;
    private bool _paired;
    private string _sourceTrace = null!;

    /// <summary>
    ///  The stable cold manifest scenario name.
    /// </summary>
    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = null!;

    /// <summary>
    ///  The stable cold manifest scenario names.
    /// </summary>
    public static IEnumerable<string> Scenarios => CliBenchmarkScenarios.ColdManifestNames;

    /// <summary>
    ///  Locates the child executable and immutable source trace.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _executable = CliProcessRunner.FindFiltraceExecutable();
        _sourceTrace = Path.Join(AppContext.BaseDirectory, "Fixtures", "activity.nettrace");
        if (!File.Exists(_sourceTrace))
        {
            throw new FileNotFoundException("The activity fixture was not copied.", _sourceTrace);
        }

        CliScenarioDefinition definition = CliBenchmarkScenarios.Get(Scenario);
        if (!definition.Cold || !definition.IsManifest)
        {
            throw new ArgumentOutOfRangeException(nameof(Scenario), Scenario, "Unknown CLI scenario.");
        }

        _caseCount = definition.CaseCount;
        _paired = definition.IsPaired;
    }

    /// <summary>
    ///  Creates fresh distinct trace paths and manifests without ETLX files.
    /// </summary>
    [IterationSetup]
    public void CreateFreshCorpus()
    {
        DisposeCorpus();
        _manifestCorpus = CliManifestCorpus.Create(
            _sourceTrace,
            _caseCount,
            _paired,
            preconvert: false);

        _arguments = CliBenchmarkScenarios.CreateArguments(
            CliBenchmarkScenarios.Get(Scenario),
            _sourceTrace,
            _manifestCorpus.BeforeManifest,
            _manifestCorpus.AfterManifest);
    }

    /// <summary>
    ///  Verifies every trace was converted and removes the corpus tree.
    /// </summary>
    [IterationCleanup]
    public void CleanupIteration()
    {
        try
        {
            if (_manifestCorpus is null)
            {
                throw new InvalidOperationException("The cold manifest corpus was not created.");
            }

            _manifestCorpus.Validate(_caseCount, _paired, expectConverted: true);
        }
        finally
        {
            DisposeCorpus();
        }
    }

    /// <summary>
    ///  Removes files left by an interrupted benchmark iteration.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup() => DisposeCorpus();

    /// <summary>
    ///  Starts filtrace once against the fresh manifest tree.
    /// </summary>
    /// <returns>A task that captures the child exit code and redirected-output sizes.</returns>
    [Benchmark]
    public Task<CliProcessResult> Run() =>
        CliProcessRunner.RunAsync(_executable, _arguments);

    private void DisposeCorpus()
    {
        _manifestCorpus?.Dispose();
        _manifestCorpus = null;
    }
}
