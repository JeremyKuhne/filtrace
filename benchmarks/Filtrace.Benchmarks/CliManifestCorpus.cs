// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text;
using System.Text.Json;
using Touki;

namespace Filtrace.Benchmarks;

/// <summary>
///  Owns distinct trace copies and manifests that prevent cache sharing between
///  batch or diff cases in out-of-process benchmarks.
/// </summary>
internal sealed partial class CliManifestCorpus : DisposableBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly List<string> _tracePaths = [];

    private CliManifestCorpus(string root, string beforeManifest, string? afterManifest)
    {
        Root = root;
        BeforeManifest = beforeManifest;
        AfterManifest = afterManifest;
    }

    /// <summary>
    ///  The temporary corpus directory deleted on disposal.
    /// </summary>
    public string Root { get; }

    /// <summary>
    ///  The batch input or baseline manifest, depending on the selected operation.
    /// </summary>
    public string BeforeManifest { get; }

    /// <summary>
    ///  The current manifest for a paired diff corpus, or <see langword="null"/> for a batch corpus.
    /// </summary>
    public string? AfterManifest { get; }

    /// <summary>
    ///  Creates independently named trace files, serializes their manifest entries,
    ///  and optionally prepares every ETLX cache before measurement.
    /// </summary>
    /// <param name="sourceTrace">The immutable capture copied for every case.</param>
    /// <param name="caseCount">The number of cases in each generated manifest.</param>
    /// <param name="paired">Whether to create both baseline and current manifest arms.</param>
    /// <param name="preconvert">Whether every trace copy must have a warm ETLX cache.</param>
    /// <returns>The disposable owner of the generated corpus tree.</returns>
    public static CliManifestCorpus Create(
        string sourceTrace,
        int caseCount,
        bool paired,
        bool preconvert)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceTrace);
        sourceTrace = Path.GetFullPath(sourceTrace);
        if (!File.Exists(sourceTrace))
        {
            throw new FileNotFoundException("The manifest source trace was not found.", sourceTrace);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(caseCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            caseCount,
            CaptureManifestBatchAnalyzer.MaxAnalyzedCases);

        string root = Path.Join(
            Path.GetTempPath(),
            $"filtrace-cli-manifest-{Guid.NewGuid():N}");

        string beforeDirectory = Path.Join(root, paired ? "before" : "batch");
        string beforeManifest = Path.Join(beforeDirectory, "manifest.json");
        string? afterDirectory = paired ? Path.Join(root, "after") : null;
        string? afterManifest = afterDirectory is null
            ? null
            : Path.Join(afterDirectory, "manifest.json");

        CliManifestCorpus corpus = new(root, beforeManifest, afterManifest);
        try
        {
            corpus.CreateArm(sourceTrace, beforeDirectory, "before", caseCount, preconvert);
            if (afterDirectory is not null)
            {
                corpus.CreateArm(sourceTrace, afterDirectory, "after", caseCount, preconvert);
            }

            corpus.Validate(caseCount, paired, expectConverted: preconvert);
            return corpus;
        }
        catch
        {
            corpus.Dispose();
            throw;
        }
    }

    /// <summary>
    ///  Verifies trace identity counts, manifest readability and pairing, and the
    ///  expected cache state for every copied trace.
    /// </summary>
    /// <param name="caseCount">The expected cases in each manifest arm.</param>
    /// <param name="paired">Whether a current arm and one-to-one case pairs are required.</param>
    /// <param name="expectConverted">Whether every copied trace must have an ETLX cache.</param>
    public void Validate(int caseCount, bool paired, bool expectConverted)
    {
        int expectedTraceCount = checked(caseCount * (paired ? 2 : 1));
        if (_tracePaths.Count != expectedTraceCount
            || _tracePaths.Distinct(PathComparer()).Count() != expectedTraceCount
            || _tracePaths.Any(static trace => !File.Exists(trace)))
        {
            throw new InvalidDataException(
                $"Corpus has {_tracePaths.Count} trace paths; expected {expectedTraceCount} distinct existing files.");
        }

        CaptureManifest before = CaptureManifestReader.Read(BeforeManifest);
        if (before.Cases.Count != caseCount)
        {
            throw new InvalidDataException(
                $"Before manifest has {before.Cases.Count} cases; expected {caseCount}.");
        }

        if (before.Cases.Select(static captureCase => captureCase.TracePath)
            .Distinct(PathComparer()).Count() != caseCount)
        {
            throw new InvalidDataException("Before manifest trace paths are not distinct.");
        }

        if (paired)
        {
            if (AfterManifest is null)
            {
                throw new InvalidDataException("Paired corpus has no after manifest.");
            }

            CaptureManifest after = CaptureManifestReader.Read(AfterManifest);
            CaptureManifestPairResult pairs = CaptureManifestPairer.Pair(before, after);
            if (pairs.Pairs.Count != caseCount || pairs.Warnings.Count != 0)
            {
                throw new InvalidDataException(
                    $"Paired corpus produced {pairs.Pairs.Count} pairs and {pairs.Warnings.Count} warnings.");
            }
        }

        foreach (string trace in _tracePaths)
        {
            bool converted = File.Exists(TraceConverter.EtlxPathFor(trace));
            if (converted != expectConverted)
            {
                throw new InvalidDataException(
                    $"Trace '{trace}' ETLX state was {converted}; expected {expectConverted}.");
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private void CreateArm(
        string sourceTrace,
        string directory,
        string arm,
        int caseCount,
        bool preconvert)
    {
        Directory.CreateDirectory(directory);
        ManifestCase[] cases = new ManifestCase[caseCount];
        for (int caseIndex = 0; caseIndex < caseCount; caseIndex++)
        {
            string traceName = $"case-{caseIndex:D2}.nettrace";
            string trace = Path.Join(directory, traceName);
            File.Copy(sourceTrace, trace);
            _tracePaths.Add(trace);
            if (preconvert)
            {
                TraceConverter.Convert(trace);
            }

            string parameters = $"Case: {caseIndex:D2}";
            cases[caseIndex] = new ManifestCase(
                $"{arm}-{caseIndex:D2}",
                "Filtrace.Benchmarks.CliManifest",
                parameters,
                $"CliManifest({parameters}): {arm}",
                traceName);
        }

        ManifestFile manifest = new(1, cases);
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(Path.Join(directory, "manifest.json"), json, Utf8);
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

}
