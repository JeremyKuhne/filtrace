// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Reflection.PortableExecutable;
using Touki;

namespace Filtrace.Benchmarks;

/// <summary>
///  Owns an equal-byte-volume symbol directory with a controlled mix of embedded
///  and external portable PDB references.
/// </summary>
internal sealed class EmbeddedPdbCorpus : DisposableBase
{
    private EmbeddedPdbCorpus(string directoryPath) => DirectoryPath = directoryPath;

    /// <summary>
    ///  The temporary symbol directory deleted when the corpus is disposed.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    ///  Builds a directory with evenly distributed embedded-PDB hits while keeping
    ///  every assembly file the same length.
    /// </summary>
    /// <param name="dllCount">The number of assembly files, from 1 through 64.</param>
    /// <param name="hitRatePercent">An exact realizable hit percentage from 0 through 100.</param>
    /// <returns>The disposable owner of the generated symbol directory.</returns>
    public static EmbeddedPdbCorpus Create(int dllCount, int hitRatePercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dllCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dllCount, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(hitRatePercent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hitRatePercent, 100);
        int embeddedProduct = checked(dllCount * hitRatePercent);
        if (embeddedProduct % 100 != 0)
        {
            throw new ArgumentException(
                $"{hitRatePercent}% is not an exact hit rate for {dllCount} DLLs.",
                nameof(hitRatePercent));
        }

        (string embeddedAssembly, string portableAssembly, long assemblyLength) = ValidateSources();
        string directory = Path.Join(
            Path.GetTempPath(),
            $"filtrace-pdb-benchmark-{Guid.NewGuid():N}");

        EmbeddedPdbCorpus corpus = new(directory);
        try
        {
            Directory.CreateDirectory(directory);
            int embeddedCount = embeddedProduct / 100;
            for (int index = 0; index < dllCount; index++)
            {
                // Spread hits through the directory so one rate is not clustered at the front.
                bool embedded = (index + 1) * embeddedCount / dllCount
                    > index * embeddedCount / dllCount;

                string source = embedded ? embeddedAssembly : portableAssembly;
                string kind = embedded ? "embedded" : "portable";
                string destination = Path.Join(directory, $"{kind}-{index:D2}.dll");
                File.Copy(source, destination);
                using FileStream stream = new(
                    destination,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None);

                // Equal bytes per DLL keep file volume independent of embedded-PDB rate.
                stream.SetLength(assemblyLength);
            }

            long actualBytes = Directory.EnumerateFiles(directory, "*.dll")
                .Sum(static path => new FileInfo(path).Length);

            long expectedBytes = checked(dllCount * assemblyLength);
            if (actualBytes != expectedBytes)
            {
                throw new InvalidOperationException(
                    $"Corpus contains {actualBytes} DLL bytes; expected {expectedBytes}.");
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
    ///  Verifies that the two source assemblies exist and expose the embedded and
    ///  external portable-PDB states required to construct a controlled corpus.
    /// </summary>
    public static void ValidateSourceAssemblies() => _ = ValidateSources();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private static (string Embedded, string Portable, long Length) ValidateSources()
    {
        string embeddedAssembly = Path.Join(AppContext.BaseDirectory, "touki.dll");
        string portableAssembly = Path.Join(AppContext.BaseDirectory, "Filtrace.Core.dll");
        ValidateEmbeddedPdb(embeddedAssembly, expected: true);
        ValidateEmbeddedPdb(portableAssembly, expected: false);
        long assemblyLength = Math.Max(
            new FileInfo(embeddedAssembly).Length,
            new FileInfo(portableAssembly).Length);

        return (embeddedAssembly, portableAssembly, assemblyLength);
    }

    private static void ValidateEmbeddedPdb(string path, bool expected)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader reader = new(stream);
        bool actual = reader.ReadDebugDirectory()
            .Any(static entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Assembly '{path}' embedded-PDB state was {actual}; expected {expected}.");
        }
    }
}
