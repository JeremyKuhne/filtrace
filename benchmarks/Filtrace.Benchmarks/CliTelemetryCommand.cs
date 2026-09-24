// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using System.Text.Json;

namespace Filtrace.Benchmarks;

/// <summary>
///  Runs repeatable out-of-process CLI campaigns and atomically writes a validated
///  JSON report for startup-cost comparisons.
/// </summary>
internal static partial class CliTelemetryCommand
{
    private const int DefaultIterations = 25;
    private const int MaximumIterations = 100;
    private const int MaximumCustomArguments = 64;
    private const int MaximumCustomArgumentLength = 8192;
    private static readonly HashSet<string> CustomReadOnlyOperations = new(
        [
            "info", "rank", "source", "report", "callers", "processes",
            "lifecycle", "tree", "classify", "timeline", "diff", "batch", "events"
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> CustomBooleanOptions = new(
        [
            "--strict", "--all-processes", "--benchmark", "--native-symbols",
            "--no-fold", "-h", "--help", "--version"
        ],
        StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly System.Text.UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    ///  The accepted telemetry command syntax printed for help requests.
    /// </summary>
    public const string Usage =
        "Usage: --cli-telemetry --scenario NAME --trace PATH --output PATH "
            + "[--iterations N] [--filtrace PATH] [--argument TOKEN ...]";

    /// <summary>
    ///  Detects the private telemetry mode before BenchmarkDotNet parses the arguments.
    /// </summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <returns><see langword="true"/> when the first token selects telemetry mode.</returns>
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "--cli-telemetry", StringComparison.Ordinal);

    /// <summary>
    ///  Detects the two supported help forms for the private telemetry mode.
    /// </summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <returns><see langword="true"/> when telemetry mode is followed only by <c>--help</c> or <c>-h</c>.</returns>
    public static bool IsHelpRequested(string[] args) =>
        args is ["--cli-telemetry", "--help" or "-h"];

    /// <summary>
    ///  Validates a campaign, prepares reusable or per-launch corpora, collects every
    ///  child observation, and writes and reads back the report.
    /// </summary>
    /// <param name="args">The telemetry selector and its scenario, trace, output, and launch options.</param>
    /// <returns>A task that completes after the report has been atomically persisted and validated.</returns>
    public static async Task RunAsync(string[] args)
    {
        TelemetryOptions options = Parse(args);
        string executable = Path.GetFullPath(
            options.FiltracePath ?? CliProcessRunner.FindFiltraceExecutable());

        string trace = Path.GetFullPath(options.TracePath);
        if (!File.Exists(trace))
        {
            throw new FileNotFoundException("The telemetry trace does not exist.", trace);
        }

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

        bool custom = options.Arguments.Count > 0;
        CliScenarioDefinition? definition = custom
            ? null
            : CliBenchmarkScenarios.Get(options.Scenario);

        CliManifestCorpus? warmCorpus = null;
        EmbeddedPdbCorpus? symbolCorpus = null;
        string[]? sharedArguments = null;
        if (custom)
        {
            sharedArguments = ResolveCustomArguments(
                options.Scenario,
                options.Arguments,
                options.TracePath,
                trace);

            EnsureOutputDoesNotAliasCustomInputs(output, sharedArguments, pathComparison);
        }
        else if (!definition!.Cold)
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
        else if (!definition!.IsManifest)
        {
            // Validate a single-trace cold scenario before launching.
            _ = CliBenchmarkScenarios.CreateArguments(definition, trace);
        }

        List<CliProcessTelemetry> launches = new(options.Iterations);
        try
        {
            for (int iteration = 1; iteration <= options.Iterations; iteration++)
            {
                CliProcessTelemetry launch;
                if (custom || !definition!.Cold)
                {
                    launch = await CliProcessRunner.RunTelemetryAsync(
                        executable,
                        sharedArguments!,
                            iteration).ConfigureAwait(continueOnCapturedContext: false);
                }
                else if (definition!.IsManifest)
                {
                    launch = await RunColdManifestAsync(
                        executable,
                        trace,
                        definition,
                            iteration).ConfigureAwait(continueOnCapturedContext: false);
                }
                else
                {
                    launch = await RunColdTraceAsync(
                        executable,
                        trace,
                        definition,
                            iteration).ConfigureAwait(continueOnCapturedContext: false);
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
            SchemaVersion: 2,
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
            || readBack.SchemaVersion != 2
            || readBack.Launches.Count != options.Iterations
            || readBack.Launches.Any(IsInvalidLaunch))
        {
            throw new InvalidDataException("CLI telemetry JSON failed readback validation.");
        }

        Console.WriteLine(output);
    }

    private static bool IsInvalidLaunch(CliProcessTelemetry launch)
    {
        if (launch.Arguments.Count == 0)
        {
            return true;
        }

        return !double.IsFinite(launch.LaunchToExitMilliseconds)
            || launch.LaunchToExitMilliseconds < 0;
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
                iteration).ConfigureAwait(continueOnCapturedContext: false);

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
                iteration).ConfigureAwait(continueOnCapturedContext: false);

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
        List<string> arguments = [];
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
                case "--argument":
                    arguments.Add(value);
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

        return new TelemetryOptions(scenario, trace, output, filtrace, iterations, arguments);
    }

    private static string[] ResolveCustomArguments(
        string scenario,
        IReadOnlyList<string> arguments,
        string requestedTrace,
        string trace)
    {
        if (!IsRecordId(scenario))
        {
            throw new ArgumentException(
                "A custom scenario must match [A-Za-z0-9][A-Za-z0-9._-]{0,63}.");
        }

        if (arguments.Count > MaximumCustomArguments)
        {
            throw new ArgumentException(
                $"A custom scenario supports at most {MaximumCustomArguments} argument tokens.");
        }

        string[] resolved = new string[arguments.Count];
        bool traceReferenced = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.Length > MaximumCustomArgumentLength || argument.IndexOf('\0') >= 0)
            {
                throw new ArgumentException(
                    $"Custom argument {index} is invalid or exceeds {MaximumCustomArgumentLength} characters.");
            }

            string resolvedArgument;
            if (string.Equals(argument, requestedTrace, StringComparison.Ordinal))
            {
                resolvedArgument = trace;
                traceReferenced = true;
            }
            else
            {
                resolvedArgument = argument;
            }

            if (resolvedArgument.Length > MaximumCustomArgumentLength)
            {
                throw new ArgumentException(
                    $"Custom argument {index} exceeds {MaximumCustomArgumentLength} characters after path normalization.");
            }

            resolved[index] = resolvedArgument;
        }

        if (resolved.Length == 0 || !CustomReadOnlyOperations.Contains(resolved[0]))
        {
            throw new ArgumentException(
                "Custom telemetry requires a read-only canonical operation as its first argument.");
        }

        if (!traceReferenced)
        {
            throw new ArgumentException(
                "Custom telemetry arguments must contain the exact path supplied by --trace.");
        }

        return resolved;
    }

    private static void EnsureOutputDoesNotAliasCustomInputs(
        string output,
        IReadOnlyList<string> arguments,
        StringComparison pathComparison)
    {
        foreach (string argument in arguments)
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(argument);
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                continue;
            }

            EnsureOutputDoesNotAliasInput(output, candidate, pathComparison);
        }

        List<string> positionalArguments = ResolveCustomPositionalArguments(
            arguments,
            out bool hasCaseId);

        if (arguments[0] == "batch" && positionalArguments.Count > 0)
        {
            EnsureOutputDoesNotAliasManifestInputs(
                output,
                positionalArguments[0],
                pathComparison);
        }
        else if (arguments[0] == "diff"
            && positionalArguments.Count > 1
            && CaptureManifestReader.IsManifestPath(positionalArguments[0])
            && CaptureManifestReader.IsManifestPath(positionalArguments[1]))
        {
            EnsureOutputDoesNotAliasManifestInputs(
                output,
                positionalArguments[0],
                pathComparison);

            EnsureOutputDoesNotAliasManifestInputs(
                output,
                positionalArguments[1],
                pathComparison);
        }
        else if (arguments[0] == "rank"
            && hasCaseId
            && positionalArguments.Count > 0)
        {
            EnsureOutputDoesNotAliasManifestInputs(
                output,
                positionalArguments[0],
                pathComparison);
        }
    }

    private static List<string> ResolveCustomPositionalArguments(
        IReadOnlyList<string> arguments,
        out bool hasCaseId)
    {
        List<string> positionalArguments = [];
        hasCaseId = false;
        bool optionsEnded = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (optionsEnded)
            {
                positionalArguments.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (argument.Length == 0 || argument[0] != '-')
            {
                positionalArguments.Add(argument);
                continue;
            }

            int valueSeparator = argument.IndexOf('=');
            string optionName = valueSeparator < 0
                ? argument
                : argument[..valueSeparator];

            hasCaseId |= optionName == "--case-id";
            if (valueSeparator < 0 && !CustomBooleanOptions.Contains(optionName))
            {
                index++;
            }
        }

        return positionalArguments;
    }

    private static void EnsureOutputDoesNotAliasManifestInputs(
        string output,
        string manifestPath,
        StringComparison pathComparison)
    {
        CaptureManifest manifest = CaptureManifestReader.Read(manifestPath);
        foreach (CaptureManifestCase captureCase in manifest.Cases)
        {
            EnsureOutputDoesNotAliasInput(output, captureCase.TracePath, pathComparison);
        }
    }

    private static void EnsureOutputDoesNotAliasInput(
        string output,
        string candidate,
        StringComparison pathComparison)
    {
        bool aliasesInput = string.Equals(candidate, output, pathComparison);
        bool aliasesEtlx = IsTracePath(candidate)
            && string.Equals(
                Path.GetFullPath(TraceConverter.EtlxPathFor(candidate)),
                output,
                pathComparison);

        if ((aliasesInput || aliasesEtlx) && File.Exists(candidate))
        {
            throw new ArgumentException(
                $"Telemetry output '{output}' must not overwrite a custom command input or its ETLX cache.");
        }
    }

    private static bool IsTracePath(string path) =>
        path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecordId(string value)
    {
        if (value.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.AsSpan(1).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-") < 0;
    }

}
