// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using System.Text.Json;

namespace Filtrace.Benchmarks;

internal static partial class CliTelemetryCommand
{
    private const int DefaultIterations = 25;
    private const int MaximumIterations = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public const string Usage =
        "Usage: --cli-telemetry --scenario NAME --trace PATH --output PATH "
        + "[--iterations N] [--filtrace PATH]";

    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "--cli-telemetry", StringComparison.Ordinal);

    public static bool IsHelpRequested(string[] args) =>
        args is ["--cli-telemetry", "--help" or "-h"];

    public static async Task RunAsync(string[] args)
    {
        TelemetryOptions options = Parse(args);
        string executable = Path.GetFullPath(
            options.FiltracePath ?? CliProcessRunner.FindFiltraceExecutable());
        string trace = Path.GetFullPath(options.TracePath);
        string output = Path.GetFullPath(options.OutputPath);
        string etlx = Path.GetFullPath(TraceConverter.EtlxPathFor(trace));
        StringComparison pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(trace, output, pathComparison)
            || string.Equals(etlx, output, pathComparison)
            || string.Equals(executable, output, pathComparison))
        {
            throw new ArgumentException(
                "The telemetry output path must differ from the trace, ETLX, and executable paths.");
        }

        string? outputDirectory = Path.GetDirectoryName(output);
        string outputName = Path.GetFileName(output);
        if (string.IsNullOrEmpty(outputDirectory) || string.IsNullOrEmpty(outputName))
        {
            throw new ArgumentException($"Telemetry output '{output}' must name a file.");
        }

        CliScenarioDefinition definition = CliBenchmarkScenarios.Get(options.Scenario);
        CliManifestCorpus? warmCorpus = null;
        EmbeddedPdbCorpus? symbolCorpus = null;
        string[]? sharedArguments = null;
        if (!definition.Cold)
        {
            if (definition.IsManifest)
            {
                warmCorpus = CliManifestCorpus.Create(
                    trace,
                    definition.CaseCount,
                    definition.IsPaired,
                    preconvert: true);
                sharedArguments = CliBenchmarkScenarios.CreateArguments(
                    definition,
                    trace,
                    warmCorpus.BeforeManifest,
                    warmCorpus.AfterManifest);
            }
            else if (definition.SymbolDllCount != 0)
            {
                symbolCorpus = EmbeddedPdbCorpus.Create(
                    definition.SymbolDllCount,
                    hitRatePercent: 100);
                sharedArguments = CliBenchmarkScenarios.CreateArguments(
                    definition,
                    trace,
                    symbolsDirectory: symbolCorpus.DirectoryPath);
                TraceConverter.Convert(trace);
            }
            else
            {
                sharedArguments = CliBenchmarkScenarios.CreateArguments(definition, trace);
                TraceConverter.Convert(trace);
            }
        }
        else if (!definition.IsManifest)
        {
            // Validate the only supported single-trace cold scenario before launching.
            _ = CliBenchmarkScenarios.CreateArguments(definition, trace);
        }

        List<CliProcessTelemetry> launches = new(options.Iterations);
        try
        {
            for (int iteration = 1; iteration <= options.Iterations; iteration++)
            {
                CliProcessTelemetry launch;
                if (!definition.Cold)
                {
                    launch = await CliProcessRunner.RunTelemetryAsync(
                        executable,
                        sharedArguments!,
                        iteration).ConfigureAwait(false);
                }
                else if (definition.IsManifest)
                {
                    launch = await RunColdManifestAsync(
                        executable,
                        trace,
                        definition,
                        iteration).ConfigureAwait(false);
                }
                else
                {
                    launch = await RunColdTraceAsync(
                        executable,
                        trace,
                        definition,
                        iteration).ConfigureAwait(false);
                }

                launches.Add(launch);
            }
        }
        finally
        {
            warmCorpus?.Dispose();
            symbolCorpus?.Dispose();
        }

        CliTelemetryReport report = new(
            SchemaVersion: 1,
            CreatedUtc: DateTimeOffset.UtcNow.ToString("O"),
            options.Scenario,
            options.Iterations,
            executable,
            launches);
        Directory.CreateDirectory(outputDirectory);
        string json = JsonSerializer.Serialize(report, JsonOptions);
        string temporaryOutput = Path.Join(
            outputDirectory,
            $".{outputName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryOutput, $"{json}{Environment.NewLine}", Utf8);
            File.Move(temporaryOutput, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryOutput))
            {
                File.Delete(temporaryOutput);
            }
        }

        CliTelemetryReport? readBack = JsonSerializer.Deserialize<CliTelemetryReport>(
            File.ReadAllText(output),
            JsonOptions);
        if (readBack is null
            || readBack.SchemaVersion != 1
            || readBack.Launches.Count != options.Iterations
            || readBack.Launches.Any(static launch => launch.Arguments.Count == 0))
        {
            throw new InvalidDataException("CLI telemetry JSON failed readback validation.");
        }

        Console.WriteLine(output);
    }

    private static async Task<CliProcessTelemetry> RunColdTraceAsync(
        string executable,
        string sourceTrace,
        CliScenarioDefinition definition,
        int iteration)
    {
        using CliColdTraceCorpus corpus = CliColdTraceCorpus.Create(sourceTrace);
        string[] arguments = CliBenchmarkScenarios.CreateArguments(definition, corpus.TracePath);
        CliProcessTelemetry telemetry = await CliProcessRunner.RunTelemetryAsync(
            executable,
            arguments,
            iteration).ConfigureAwait(false);
        corpus.ValidateConverted();
        return telemetry;
    }

    private static async Task<CliProcessTelemetry> RunColdManifestAsync(
        string executable,
        string sourceTrace,
        CliScenarioDefinition definition,
        int iteration)
    {
        using CliManifestCorpus corpus = CliManifestCorpus.Create(
            sourceTrace,
            definition.CaseCount,
            definition.IsPaired,
            preconvert: false);
        string[] arguments = CliBenchmarkScenarios.CreateArguments(
            definition,
            sourceTrace,
            corpus.BeforeManifest,
            corpus.AfterManifest);
        CliProcessTelemetry telemetry = await CliProcessRunner.RunTelemetryAsync(
            executable,
            arguments,
            iteration).ConfigureAwait(false);
        corpus.Validate(
            definition.CaseCount,
            definition.IsPaired,
            expectConverted: true);
        return telemetry;
    }

    private static TelemetryOptions Parse(string[] args)
    {
        string? scenario = null;
        string? trace = null;
        string? output = null;
        string? filtrace = null;
        int iterations = DefaultIterations;
        for (int index = 1; index < args.Length; index++)
        {
            string name = args[index];
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{name}' requires a value.");
            }

            string value = args[++index];
            switch (name)
            {
                case "--scenario":
                    scenario = value;
                    break;
                case "--trace":
                    trace = value;
                    break;
                case "--output":
                    output = value;
                    break;
                case "--filtrace":
                    filtrace = value;
                    break;
                case "--iterations":
                    if (!int.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out iterations))
                    {
                        throw new ArgumentException(
                            $"Option '--iterations' requires an integer value; got '{value}'.");
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown option or value '{name} {value}'.");
            }
        }

        if (string.IsNullOrEmpty(scenario)
            || string.IsNullOrEmpty(trace)
            || string.IsNullOrEmpty(output))
        {
            throw new ArgumentException(Usage);
        }

        if (iterations is < 1 or > MaximumIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                iterations,
                $"Iterations must be in [1, {MaximumIterations}].");
        }

        return new TelemetryOptions(scenario, trace, output, filtrace, iterations);
    }

}
