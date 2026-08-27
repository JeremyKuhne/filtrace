// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Measures trace loading while scanning controlled symbol directories.
/// </summary>
[MemoryDiagnoser]
public class EmbeddedPdbBenchmarks
{
    private static readonly PdbScenario[] PdbScenarios =
    [
        new("dll1-hit0", 1, 0),
        new("dll1-hit100", 1, 100),
        new("dll8-hit0", 8, 0),
        new("dll8-hit25", 8, 25),
        new("dll8-hit100", 8, 100),
        new("dll32-hit0", 32, 0),
        new("dll32-hit25", 32, 25),
        new("dll32-hit100", 32, 100),
        new("dll64-hit0", 64, 0),
        new("dll64-hit25", 64, 25),
        new("dll64-hit100", 64, 100)
    ];

    private string _activityTrace = null!;
    private EmbeddedPdbCorpus? _corpus;
    private PdbScenario _selected = null!;

    /// <summary>
    ///  The DLL count and exact embedded-PDB hit rate.
    /// </summary>
    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = null!;

    /// <summary>
    ///  The feasible symbol-directory scenarios.
    /// </summary>
    public static IEnumerable<string> Scenarios =>
        PdbScenarios.Select(static scenario => scenario.Name);

    /// <summary>
    ///  Validates the source binaries and preconverts the trace.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _activityTrace = Path.Join(AppContext.BaseDirectory, "Fixtures", "activity.nettrace");
        _selected = PdbScenarios.Single(scenario => scenario.Name == Scenario);

        EmbeddedPdbCorpus.ValidateSourceAssemblies();
        LoadedTrace trace = new TraceLoader().Load(_activityTrace);
        if (trace.Info.SampleCount <= 0)
        {
            throw new InvalidOperationException("The activity fixture produced no CPU samples.");
        }
    }

    /// <summary>
    ///  Creates a fresh controlled symbol directory for each iteration.
    /// </summary>
    [IterationSetup]
    public void CreateSymbolsDirectory()
    {
        DeleteSymbolsDirectory();
        _corpus = EmbeddedPdbCorpus.Create(_selected.DllCount, _selected.HitRatePercent);
    }

    /// <summary>
    ///  Removes the per-iteration symbol directory.
    /// </summary>
    [IterationCleanup]
    public void CleanupIteration() => DeleteSymbolsDirectory();

    /// <summary>
    ///  Removes a symbol directory left by an interrupted iteration.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup() => DeleteSymbolsDirectory();

    /// <summary>
    ///  Loads the trace through the public symbol-directory path.
    /// </summary>
    [Benchmark]
    public LoadedTrace LoadWithSymbols() =>
        new TraceLoader().Load(_activityTrace, _corpus!.DirectoryPath);

    private void DeleteSymbolsDirectory()
    {
        _corpus?.Dispose();
        _corpus = null;
    }

    private sealed record PdbScenario(string Name, int DllCount, int HitRatePercent);
}
