// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Touki;

namespace Filtrace.Benchmarks;

internal sealed class CliColdTraceCorpus : DisposableBase
{
    private CliColdTraceCorpus(string root, string tracePath)
    {
        Root = root;
        TracePath = tracePath;
    }

    public string Root { get; }

    public string TracePath { get; }

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
