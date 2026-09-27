// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.Tracing;

/// <summary>
///  Binds an ETLX cache to the converter, backend binary, and source and cache file identities.
/// </summary>
internal static class EtlxCacheProvenance
{
    /// <summary>
    ///  The maximum provenance marker size in bytes.
    /// </summary>
    internal const int MaxBytes = 4096;

    /// <summary>
    ///  The conversion option whose value is recorded in every ready marker.
    /// </summary>
    internal const bool ContinueOnError = true;

    private const int SchemaVersion = 1;
    private const int ConversionEpoch = 1;
    private const string Producer = "filtrace";
    private const string Pending = "pending";
    private const string Ready = "ready";

    private static readonly string s_backendAssembly = typeof(EtlxTraceLog).Assembly.FullName
        ?? throw new InvalidOperationException("The ETLX backend assembly has no identity.");

    private static readonly string s_backendModule = typeof(EtlxTraceLog)
        .Assembly.ManifestModule.ModuleVersionId.ToString("D");

    /// <summary>
    ///  Derives the Filtrace-owned marker path from the public ETLX cache path.
    /// </summary>
    /// <param name="cachePath">The ETLX cache path.</param>
    /// <returns>The adjacent provenance marker path.</returns>
    internal static string PathFor(string cachePath) => $"{cachePath}.filtrace.json";

    /// <summary>
    ///  Checks whether a cache can be reused, must be rebuilt, or needs explicit cleanup.
    /// </summary>
    /// <param name="sourcePath">The source trace path.</param>
    /// <param name="cachePath">The adjacent ETLX path.</param>
    /// <returns>The state of the cache and provenance marker.</returns>
    internal static EtlxProvenanceState Inspect(string sourcePath, string cachePath)
    {
        string markerPath = PathFor(cachePath);
        bool cacheExists = File.Exists(cachePath);
        if (!File.Exists(markerPath))
        {
            if (!cacheExists)
            {
                return EtlxProvenanceState.Missing;
            }

            throw Unverified(sourcePath, cachePath, "has no Filtrace provenance marker");
        }

        try
        {
            using FileStream stream = new(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes)
            {
                throw new JsonException($"The provenance marker exceeds {MaxBytes} bytes.");
            }

            using JsonDocument document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = 4 });

            JsonElement root = document.RootElement;
            RequireObject(root, "provenance marker");
            RequireUniqueProperties(root);

            if (RequireLong(root, "schemaVersion") != SchemaVersion
                || RequireString(root, "producer") != Producer)
            {
                throw new JsonException("Unsupported ETLX provenance schema or producer.");
            }

            string state = RequireString(root, "state");
            if (state is not (Pending or Ready))
            {
                throw new JsonException($"Unknown ETLX provenance state '{state}'.");
            }

            if (!cacheExists || state == Pending)
            {
                return EtlxProvenanceState.Interrupted;
            }

            if (RequireLong(root, "conversionEpoch") != ConversionEpoch
                || RequireString(root, "backendAssembly") != s_backendAssembly
                || RequireString(root, "backendModule") != s_backendModule
                || RequireBool(root, "continueOnError") != ContinueOnError)
            {
                throw Unverified(sourcePath, cachePath, "was produced with different conversion semantics");
            }

            EtlxFileIdentity recordedCache = RequireIdentity(root, "cache");
            if (recordedCache.Length == 0
                || recordedCache != EtlxFileIdentity.ReadExisting(cachePath))
            {
                throw Unverified(sourcePath, cachePath, "does not match its provenance marker");
            }

            EtlxFileIdentity recordedSource = RequireIdentity(root, "source");
            return recordedSource == EtlxFileIdentity.ReadExisting(sourcePath)
                ? EtlxProvenanceState.Current
                : EtlxProvenanceState.StaleSource;
        }
        catch (JsonException exception)
        {
            throw Unverified(
                sourcePath,
                cachePath,
                $"has an invalid Filtrace provenance marker '{markerPath}': {exception.Message}",
                exception);
        }
    }

    /// <summary>
    ///  Writes an invalidating marker before ETLX publication.
    /// </summary>
    /// <param name="path">A unique temporary marker path.</param>
    internal static void WritePending(string path)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("producer", Producer);
        writer.WriteString("state", Pending);
        writer.WriteEndObject();
    }

    /// <summary>
    ///  Writes the final identity marker after ETLX publication.
    /// </summary>
    /// <param name="path">A unique temporary marker path.</param>
    /// <param name="source">The source identity observed during conversion.</param>
    /// <param name="cache">The published ETLX identity.</param>
    internal static void WriteReady(
        string path,
        EtlxFileIdentity source,
        EtlxFileIdentity cache)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("producer", Producer);
        writer.WriteString("state", Ready);
        writer.WriteNumber("conversionEpoch", ConversionEpoch);
        writer.WriteString("backendAssembly", s_backendAssembly);
        writer.WriteString("backendModule", s_backendModule);
        writer.WriteBoolean("continueOnError", ContinueOnError);
        WriteIdentity(writer, "source", source);
        WriteIdentity(writer, "cache", cache);
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, string name, EtlxFileIdentity identity)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("length", identity.Length);
        writer.WriteNumber("lastWriteTimeUtcTicks", identity.LastWriteTimeUtcTicks);
        writer.WriteEndObject();
    }

    private static EtlxFileIdentity RequireIdentity(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement identity))
        {
            throw new JsonException($"ETLX provenance is missing '{name}'.");
        }

        RequireObject(identity, name);
        RequireUniqueProperties(identity);
        long length = RequireLong(identity, "length");
        long ticks = RequireLong(identity, "lastWriteTimeUtcTicks");
        if (length < 0 || ticks <= 0)
        {
            throw new JsonException($"ETLX provenance '{name}' has invalid file metadata.");
        }

        return new EtlxFileIdentity(length, ticks);
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"ETLX provenance '{name}' must be an object.");
        }
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException($"Duplicate ETLX provenance field '{property.Name}'.");
            }
        }
    }

    private static long RequireLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long result))
        {
            throw new JsonException($"ETLX provenance '{name}' must be an integer.");
        }

        return result;
    }

    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not string result
            || result.Length == 0)
        {
            throw new JsonException($"ETLX provenance '{name}' must be a nonempty string.");
        }

        return result;
    }

    private static bool RequireBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException($"ETLX provenance '{name}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static IOException Unverified(
        string sourcePath,
        string cachePath,
        string reason,
        Exception? innerException = null)
    {
        string message =
            $"ETLX cache '{cachePath}' {reason}. It was not reused or removed. "
                + $"Run 'filtrace cache <trace> --action clean' (trace: '{sourcePath}') "
                + "to discard it explicitly, then retry.";

        return new IOException(message, innerException);
    }
}
