// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.Tracing;

/// <summary>Reads the bounded schema-v1 manifests emitted by the capture helper.</summary>
public static class CaptureManifestReader
{
    /// <summary>Maximum UTF-8 manifest size accepted by the analyzer.</summary>
    public const int MaxManifestBytes = 20 * 1024;

    /// <summary>Maximum cases accepted from one manifest.</summary>
    public const int MaxCases = 256;

    private const int MaxIdLength = 256;
    private const int MaxBenchmarkLength = 512;
    private const int MaxParametersLength = 512;
    private const int MaxDisplayLength = 1024;
    private const int MaxProcessLength = 256;
    private const int MaxOperationUnitLength = 64;

    /// <summary>Whether a path names the capture helper's manifest artifact.</summary>
    public static bool IsManifestPath(string path) =>
        string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads and validates a capture manifest.</summary>
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
            if (!root.TryGetProperty("schemaVersion", out JsonElement schema)
                || schema.ValueKind != JsonValueKind.Number
                || schema.GetInt32() != 1)
            {
                throw new InvalidDataException("Capture manifest schemaVersion must be 1.");
            }

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
                string id = RequiredBoundedString(caseElement, "id", MaxIdLength);
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
                    trace ?? speedscope
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
                    operationUnit));
            }

            return new CaptureManifest(fullPath, process, cases);
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

    internal static string ExtractParameters(string display)
    {
        int close = display.LastIndexOf("): ", StringComparison.Ordinal);
        if (close < 0)
        {
            return string.Empty;
        }

        int open = display.IndexOf('(');
        return open >= 0 && open < close ? display[(open + 1)..close] : string.Empty;
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
            : Path.Combine(manifestDirectory, path));
}