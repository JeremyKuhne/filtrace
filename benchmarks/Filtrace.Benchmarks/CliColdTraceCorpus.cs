// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Touki;

namespace Filtrace.Benchmarks;

/// <summary>
///  Owns an isolated trace copy whose unique path prevents an existing ETLX cache
///  from warming a cold-process benchmark.
/// </summary>
internal sealed class CliColdTraceCorpus : DisposableBase
{
    private CliColdTraceCorpus(string root, string tracePath)
    {
        Root = root;
        TracePath = tracePath;
    }

    /// <summary>
    ///  The temporary directory deleted when the corpus is disposed.
    /// </summary>
    public string Root { get; }

    /// <summary>
    ///  The isolated trace path passed to the measured child process.
    /// </summary>
    public string TracePath { get; }

    /// <summary>
    ///  Copies a source capture to a unique temporary path and verifies that no
    ///  adjacent ETLX cache already exists.
    /// </summary>
    /// <param name="sourceTrace">The immutable capture to copy.</param>
    /// <returns>The disposable owner of the isolated trace and directory.</returns>
    public static CliColdTraceCorpus Create(string sourceTrace)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceTrace);
        sourceTrace = Path.GetFullPath(sourceTrace);
        if (!File.Exists(sourceTrace))
        {
            throw new FileNotFoundException("The cold source trace was not found.", sourceTrace);
        }

        string root = Path.Join(
            Path.GetTempPath(),
            $"filtrace-cli-cold-{Guid.NewGuid():N}");

        string trace = Path.Join(root, "activity.nettrace");
        CliColdTraceCorpus corpus = new(root, trace);
        try
        {
            Directory.CreateDirectory(root);
            File.Copy(sourceTrace, trace);
            if (File.Exists(TraceConverter.EtlxPathFor(trace)))
            {
                throw new InvalidOperationException("The cold trace unexpectedly has an ETLX cache.");
            }

            return corpus;
        }
        catch
        {
            corpus.Dispose();
            throw;
        }
    }

    /// <summary>
    ///  Verifies that the measured invocation created an ETLX cache beside the copy.
    /// </summary>
    public void ValidateConverted()
    {
        if (!File.Exists(TraceConverter.EtlxPathFor(TracePath)))
        {
            throw new InvalidOperationException("The cold CLI invocation did not create an ETLX cache.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
