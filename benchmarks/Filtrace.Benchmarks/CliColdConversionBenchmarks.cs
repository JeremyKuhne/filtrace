// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>Measures one filtrace process against a fresh trace with no ETLX cache.</summary>
[MemoryDiagnoser]
public class CliColdConversionBenchmarks
{
    private string[] _arguments = null!;
    private CliColdTraceCorpus? _corpus;
    private string _executable = null!;
    private string _sourceTrace = null!;

    /// <summary>Locates the child executable and immutable source trace.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _executable = CliProcessRunner.FindFiltraceExecutable();
        _sourceTrace = Path.Combine(AppContext.BaseDirectory, "Fixtures", "activity.nettrace");
        if (!File.Exists(_sourceTrace))
        {
            throw new FileNotFoundException("The activity fixture was not copied.", _sourceTrace);
        }
    }

    /// <summary>Creates one fresh trace identity with no adjacent ETLX cache.</summary>
    [IterationSetup]
    public void CreateFreshTrace()
    {
        DisposeCorpus();
        _corpus = CliColdTraceCorpus.Create(_sourceTrace);
        _arguments = CliBenchmarkScenarios.CreateArguments(
            CliBenchmarkScenarios.Get("info-cold"),
            _corpus.TracePath);
    }

    /// <summary>Verifies conversion and removes the fresh trace identity.</summary>
    [IterationCleanup]
    public void CleanupIteration()
    {
        try
        {
            if (_corpus is null)
            {
                throw new InvalidOperationException("The cold trace corpus was not created.");
            }

            _corpus.ValidateConverted();
        }
        finally
        {
            DisposeCorpus();
        }
    }

    /// <summary>Removes files left by an interrupted benchmark iteration.</summary>
    [GlobalCleanup]
    public void Cleanup() => DisposeCorpus();

    /// <summary>Starts filtrace once against the fresh trace.</summary>
    [Benchmark]
    public Task<CliProcessResult> InfoCold() =>
        CliProcessRunner.RunAsync(_executable, _arguments);

    private void DisposeCorpus()
    {
        _corpus?.Dispose();
        _corpus = null;
    }
}