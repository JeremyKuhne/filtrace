// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures fresh filtrace processes against a prepared warm ETLX cache.
/// </summary>
[MemoryDiagnoser]
public class CliWarmBenchmarks
{
    private string[] _arguments = null!;
    private string _executable = null!;
    private CliManifestCorpus? _manifestCorpus;
    private EmbeddedPdbCorpus? _symbolCorpus;
    private string _trace = null!;

    /// <summary>
    ///  The stable warm CLI scenario name.
    /// </summary>
    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = null!;

    /// <summary>
    ///  The stable warm CLI scenario names.
    /// </summary>
    public static IEnumerable<string> Scenarios => CliBenchmarkScenarios.WarmNames;

    /// <summary>
    ///  Preconverts the trace and validates the selected CLI invocation.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _executable = CliProcessRunner.FindFiltraceExecutable();
        _trace = Path.Join(AppContext.BaseDirectory, "Fixtures", "activity.nettrace");
        CliScenarioDefinition definition = CliBenchmarkScenarios.Get(Scenario);
        if (definition.IsManifest)
        {
            _manifestCorpus = CliManifestCorpus.Create(
                _trace,
                definition.CaseCount,
                definition.IsPaired,
                preconvert: true);

            _arguments = CliBenchmarkScenarios.CreateArguments(
                definition,
                _trace,
                _manifestCorpus.BeforeManifest,
                _manifestCorpus.AfterManifest);
        }
        else if (definition.SymbolDllCount != 0)
        {
            TraceConverter.Clean(_trace);
            TraceConverter.Convert(_trace);
            _symbolCorpus = EmbeddedPdbCorpus.Create(definition.SymbolDllCount, hitRatePercent: 100);
            _arguments = CliBenchmarkScenarios.CreateArguments(
                definition,
                _trace,
                symbolsDirectory: _symbolCorpus.DirectoryPath);
        }
        else
        {
            TraceConverter.Clean(_trace);
            TraceConverter.Convert(_trace);
            _arguments = CliBenchmarkScenarios.CreateArguments(definition, _trace);
        }

        try
        {
            _ = CliProcessRunner.RunAsync(_executable, _arguments)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>
    ///  Removes any generated manifest corpus.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _manifestCorpus?.Dispose();
        _manifestCorpus = null;
        _symbolCorpus?.Dispose();
        _symbolCorpus = null;
    }

    /// <summary>
    ///  Starts filtrace and consumes its redirected output streams.
    /// </summary>
    /// <returns>A task that captures the child exit code and redirected-output sizes.</returns>
    [Benchmark]
    public Task<CliProcessResult> Run() =>
        CliProcessRunner.RunAsync(_executable, _arguments);
}
