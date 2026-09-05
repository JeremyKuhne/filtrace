// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.Tracing;

/// <summary>
///  Reads recorder-established analysis enablement from a bounded JSON sidecar.
/// </summary>
internal static class CaptureMetadataReader
{
    /// <summary>
    ///  The maximum metadata sidecar size accepted, in bytes.
    /// </summary>
    internal const int MaxBytes = 64 * 1024;

    /// <summary>
    ///  Constructs the recorder metadata sidecar path for a trace.
    /// </summary>
    /// <param name="tracePath">The trace path to which the sidecar suffix is appended.</param>
    /// <returns>The corresponding <c>.filtrace.json</c> path.</returns>
    public static string PathFor(string tracePath) => $"{tracePath}.filtrace.json";

    /// <summary>
    ///  Reads known analysis enablement from a bounded sidecar, degrading malformed metadata to a warning.
    /// </summary>
    /// <param name="tracePath">The trace whose sidecar should be read.</param>
    /// <param name="warnings">The collection that receives a read or validation warning.</param>
    /// <returns>
    ///  Known capture statuses, or <see langword="null"/> when the sidecar is absent or cannot be trusted.
    /// </returns>
    public static IReadOnlyDictionary<string, CaptureStatus>? Read(
        string tracePath,
        List<string> warnings)
    {
        string metadataPath = PathFor(tracePath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            byte[] bytes = ReadBounded(metadataPath);
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions { MaxDepth = 8 });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out int version)
                || version != 1
                || !root.TryGetProperty("analyses", out JsonElement analyses)
                || analyses.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Expected schemaVersion 1 and an analyses object.");
            }

            Dictionary<string, CaptureStatus> statuses = new(StringComparer.Ordinal);
            foreach (JsonProperty property in analyses.EnumerateObject())
            {
                if (!TraceCapabilities.IsKnownAnalysis(property.Name))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String
                    || !TryParseStatus(property.Value.GetString(), out CaptureStatus status))
                {
                    throw new JsonException(
                        $"Analysis '{property.Name}' must be 'enabled', 'disabled', or 'unknown'.");
                }

                statuses[property.Name] = status;
            }

            return statuses;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException)
        {
            warnings.Add(
                $"Capture metadata '{metadataPath}' could not be read: {exception.Message} "
                    + "Provider enablement remains unknown where no events were observed.");

            return null;
        }
    }

    private static byte[] ReadBounded(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[MaxBytes + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read > MaxBytes || stream.ReadByte() != -1)
        {
            throw new InvalidDataException($"Capture metadata exceeds {MaxBytes} bytes.");
        }

        return buffer.AsSpan(0, read).ToArray();
    }

    private static bool TryParseStatus(string? value, out CaptureStatus status)
    {
        status = value switch
        {
            "enabled" => CaptureStatus.Enabled,
            "disabled" => CaptureStatus.Disabled,
            "unknown" => CaptureStatus.Unknown,
            _ => CaptureStatus.Unknown
        };

        return value is "enabled" or "disabled" or "unknown";
    }
}
