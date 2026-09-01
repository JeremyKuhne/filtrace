// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Globalization;
using System.Text.Json;

namespace Filtrace.Tracing;

/// <summary>
///  Reads the bounded schema-v1 manifests emitted by the capture helper.
/// </summary>
public static class CaptureManifestReader
{
    /// <summary>
    ///  Maximum UTF-8 manifest size accepted by the analyzer.
    /// </summary>
    public const int MaxManifestBytes = 16 * 1024 * 1024;

    /// <summary>
    ///  Maximum cases accepted from one manifest.
    /// </summary>
    public const int MaxCases = 256;

    /// <summary>
    ///  Maximum characters accepted in a manifest case identifier.
    /// </summary>
    public const int MaxCaseIdLength = 256;

    private const int MaxBenchmarkLength = 512;
    private const int MaxParametersLength = 512;
    private const int MaxDisplayLength = 1024;
    private const int MaxProcessLength = 256;
    private const int MaxOperationUnitLength = 64;

    private const int MaxKindLength = 32;

    /// <summary>
    ///  Matches the iteration ceiling the <c>collect</c> verb accepts, so a manifest can
    ///  describe any capture the tool can produce and no more.
    /// </summary>
    private const int MaxInvocations = 1000;

    /// <summary>
    ///  Whether a path names the capture helper's manifest artifact.
    /// </summary>
    /// <param name="path">The candidate file path.</param>
    /// <returns><see langword="true"/> when the final path segment is <c>manifest.json</c>, ignoring case.</returns>
    public static bool IsManifestPath(string path) =>
        string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///  Reads and validates a capture manifest.
    /// </summary>
    /// <param name="path">Path to <c>manifest.json</c>.</param>
    /// <returns>The parsed manifest with canonical case paths.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">The manifest does not exist.</exception>
    /// <exception cref="InvalidDataException">The manifest is malformed, oversized, or unsupported.</exception>
    public static CaptureManifest Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        FileInfo file = new(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Capture manifest not found: {fullPath}", fullPath);
        }

        if (file.Length >= MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"Capture manifest is {file.Length} bytes; it must be under {MaxManifestBytes} bytes.");
        }

        try
        {
            byte[] utf8 = File.ReadAllBytes(fullPath);
            if (utf8.Length >= MaxManifestBytes)
            {
                throw new InvalidDataException(
                    $"Capture manifest is {utf8.Length} bytes; it must be under {MaxManifestBytes} bytes.");
            }

            using JsonDocument document = JsonDocument.Parse(
                utf8,
                new JsonDocumentOptions { MaxDepth = 16 });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Capture manifest must be a JSON object.");
            }

            ValidateUniqueProperties(root, "root");

            // Version 2 added the kind discriminator and per-case invocations. Both are
            // additive, so a version 1 manifest still reads and keeps working with batch
            // and diff rather than needing a parallel consumer.
            if (!root.TryGetProperty("schemaVersion", out JsonElement schema)
                || schema.ValueKind != JsonValueKind.Number
                || schema.GetInt32() is not (1 or 2))
            {
                throw new InvalidDataException("Capture manifest schemaVersion must be 1 or 2.");
            }

            CaptureKind kind = ReadKind(root);

            if (!root.TryGetProperty("cases", out JsonElement casesElement)
                || casesElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Capture manifest must contain a cases array.");
            }

            int caseCount = casesElement.GetArrayLength();
            if (caseCount > MaxCases)
            {
                throw new InvalidDataException(
                    $"Capture manifest has {caseCount} cases; the maximum is {MaxCases}.");
            }

            string? process = OptionalBoundedString(root, "process", MaxProcessLength);
            string manifestDirectory = Path.GetDirectoryName(fullPath)!;
            List<CaptureManifestCase> cases = new(caseCount);
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (JsonElement caseElement in casesElement.EnumerateArray())
            {
                if (caseElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Every capture manifest case must be an object.");
                }

                ValidateUniqueProperties(caseElement, "case");
                string id = RequiredBoundedString(caseElement, "id", MaxCaseIdLength);
                if (!ids.Add(id))
                {
                    throw new InvalidDataException($"Capture manifest contains duplicate case id '{id}'.");
                }

                string? benchmark = OptionalBoundedString(
                    caseElement,
                    "benchmark",
                    MaxBenchmarkLength);

                string display = OptionalBoundedString(
                    caseElement,
                    "benchmarkDisplay",
                    MaxDisplayLength) ?? benchmark ?? id;

                string parameters = OptionalBoundedString(
                    caseElement,
                    "parameters",
                    MaxParametersLength,
                    allowEmpty: true) ?? ExtractParameters(display);

                if (parameters.Length > MaxParametersLength)
                {
                    throw new InvalidDataException(
                        $"Capture manifest field 'parameters' must contain 0-{MaxParametersLength} characters.");
                }

                string? trace = OptionalBoundedString(caseElement, "trace", MaxDisplayLength);
                string? speedscope = OptionalBoundedString(
                    caseElement,
                    "speedscope",
                    MaxDisplayLength);

                string analysisPath = ResolvePath(
                    manifestDirectory,
                    trace
                        ?? speedscope
                        ?? throw new InvalidDataException($"Capture case '{id}' has no trace or speedscope path."));

                string? symbols = OptionalBoundedString(
                    caseElement,
                    "symbolsDirectory",
                    MaxDisplayLength);

                double? operationCount = OptionalPositiveFiniteDouble(caseElement, "operationCount");
                string? operationUnit = OptionalBoundedString(
                    caseElement,
                    "operationUnit",
                    MaxOperationUnitLength);

                cases.Add(new CaptureManifestCase(
                    id,
                    benchmark,
                    parameters,
                    display,
                    analysisPath,
                    symbols is null ? null : ResolvePath(manifestDirectory, symbols),
                    operationCount,
                    operationUnit)
                {
                    Invocations = ReadInvocations(caseElement, id)
                });
            }

            return new CaptureManifest(fullPath, process, cases) { Kind = kind };
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or FormatException
                or OverflowException)
        {
            throw new InvalidDataException("Capture manifest JSON is malformed.", exception);
        }
    }

    /// <summary>
    ///  Reads the manifest kind, defaulting to a benchmark capture when none is recorded.
    /// </summary>
    private static CaptureKind ReadKind(JsonElement root)
    {
        string? kind = OptionalBoundedString(root, "kind", MaxKindLength);
        return kind switch
        {
            null or "benchmark" => CaptureKind.Benchmark,
            "command" => CaptureKind.Command,
            _ => throw new InvalidDataException(
                $"Capture manifest kind '{kind}' is not recognized; expected 'benchmark' or 'command'.")
        };
    }

    /// <summary>
    ///  Reads a case's launches, which a benchmark case does not carry.
    /// </summary>
    private static IReadOnlyList<CaptureInvocation> ReadInvocations(JsonElement caseElement, string id)
    {
        if (!caseElement.TryGetProperty("invocations", out JsonElement element))
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Capture case '{id}' has a non-array invocations property.");
        }

        int count = element.GetArrayLength();
        if (count > MaxInvocations)
        {
            throw new InvalidDataException(
                $"Capture case '{id}' has {count} invocations; the maximum is {MaxInvocations}.");
        }

        List<CaptureInvocation> invocations = new(count);
        foreach (JsonElement invocationElement in element.EnumerateArray())
        {
            if (invocationElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Every invocation of case '{id}' must be an object.");
            }

            ValidateUniqueProperties(invocationElement, "invocation");
            int processId = RequiredInt32(invocationElement, "processId", id);
            if (processId <= 0)
            {
                throw new InvalidDataException(
                    $"Capture case '{id}' has an invocation whose processId must be positive.");
            }

            invocations.Add(new CaptureInvocation(
                RequiredInt32(invocationElement, "ordinal", id),
                processId,
                RequiredInt32(invocationElement, "exitCode", id),
                RequiredTimestamp(invocationElement, "startedUtc", id),
                RequiredTimestamp(invocationElement, "stoppedUtc", id)));
        }

        return invocations;
    }

    private static int RequiredInt32(JsonElement element, string name, string id)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int number))
        {
            throw new InvalidDataException($"Capture case '{id}' has an invocation without an integer '{name}'.");
        }

        return number;
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement element, string name, string id)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {
            throw new InvalidDataException(
                $"Capture case '{id}' has an invocation without a round-trip '{name}' timestamp.");
        }

        return timestamp;
    }

    /// <summary>
    ///  Extracts the parameter segment from BenchmarkDotNet display text in parenthesized or bracketed form.
    /// </summary>
    /// <param name="display">The benchmark display text.</param>
    /// <returns>The extracted parameter text, or an empty string when the display has no recognized segment.</returns>
    internal static string ExtractParameters(string display)
    {
        string trimmedDisplay = display.TrimEnd();
        if (trimmedDisplay.EndsWith(']'))
        {
            int openBracket = trimmedDisplay.LastIndexOf('[');
            if (openBracket >= 0)
            {
                return trimmedDisplay[(openBracket + 1)..^1];
            }
        }

        int close = trimmedDisplay.LastIndexOf("): ", StringComparison.Ordinal);
        if (close < 0)
        {
            return string.Empty;
        }

        int open = trimmedDisplay.IndexOf('(');
        return open >= 0 && open < close ? trimmedDisplay[(open + 1)..close] : string.Empty;
    }

    private static string RequiredBoundedString(JsonElement element, string name, int maxLength) =>
        OptionalBoundedString(element, name, maxLength)
            ?? throw new InvalidDataException($"Capture manifest field '{name}' is required.");

    private static string? OptionalBoundedString(
        JsonElement element,
        string name,
        int maxLength,
        bool allowEmpty = false)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Capture manifest field '{name}' must be a string or null.");
        }

        string text = value.GetString()!;
        if ((!allowEmpty && text.Length == 0)
            || text.Length > maxLength
            || text.Any(char.IsControl))
        {
            int minimumLength = allowEmpty ? 0 : 1;
            throw new InvalidDataException(
                $"Capture manifest field '{name}' must contain {minimumLength}-{maxLength} non-control characters.");
        }

        return text;
    }

    private static double? OptionalPositiveFiniteDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double number)
            || !double.IsFinite(number)
            || number <= 0.0)
        {
            throw new InvalidDataException(
                $"Capture manifest field '{name}' must be a positive finite number or null.");
        }

        return number;
    }

    private static void ValidateUniqueProperties(JsonElement element, string context)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Capture manifest {context} contains duplicate property '{property.Name}'.");
            }
        }
    }

    private static string ResolvePath(string manifestDirectory, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path)
            ? path
            : Path.Join(manifestDirectory, path));
}
