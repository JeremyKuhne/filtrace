// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Filtrace.Tracing.Readers;
using Touki;

namespace Filtrace.Core.Tests;

[TestClass]
public sealed partial class EmbeddedPdbExtractorTests
{
    private static string EmbeddedAssembly =>
        Path.Join(AppContext.BaseDirectory, "touki.dll");

    private static string PortableAssembly =>
        Path.Join(AppContext.BaseDirectory, "Filtrace.Core.dll");

    [TestMethod]
    public void Extract_EmbeddedPdb_WritesPortablePdb()
    {
        using TemporaryDirectory input = new();
        File.Copy(EmbeddedAssembly, Path.Join(input.Path, "embedded.dll"));

        string? output = EmbeddedPdbExtractor.Extract(input.Path);

        try
        {
            output.Should().NotBeNull();
            string pdb = Path.Join(output!, "embedded.pdb");
            File.Exists(pdb).Should().BeTrue();
            using FileStream stream = File.OpenRead(pdb);
            using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            provider.GetMetadataReader().Documents.Count.Should().BeGreaterThan(0);
        }
        finally
        {
            DeleteOutput(output);
        }
    }

    [TestMethod]
    public void Extract_NoEmbeddedPdb_ReturnsNull()
    {
        using TemporaryDirectory input = new();
        File.Copy(PortableAssembly, Path.Join(input.Path, "portable.dll"));

        EmbeddedPdbExtractor.Extract(input.Path).Should().BeNull();
    }

    [TestMethod]
    public void Extract_CorruptDll_ReturnsNull()
    {
        using TemporaryDirectory input = new();
        File.WriteAllText(Path.Join(input.Path, "corrupt.dll"), "not a PE image");

        EmbeddedPdbExtractor.Extract(input.Path).Should().BeNull();
    }

    [TestMethod]
    public void Extract_TruncatedEmbeddedPdb_ReturnsNull()
    {
        using TemporaryDirectory input = new();
        string path = Path.Join(input.Path, "truncated.dll");
        CopyTruncatedEmbeddedAssembly(path);

        EmbeddedPdbExtractor.Extract(input.Path).Should().BeNull();
    }

    [TestMethod]
    public void Extract_LockedDll_ReturnsNull()
    {
        using TemporaryDirectory input = new();
        string path = Path.Join(input.Path, "locked.dll");
        File.Copy(EmbeddedAssembly, path);
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        EmbeddedPdbExtractor.Extract(input.Path).Should().BeNull();
    }

    [TestMethod]
    public void Extract_DuplicateAssemblyCopies_UsesInputFileNames()
    {
        using TemporaryDirectory input = new();
        File.Copy(EmbeddedAssembly, Path.Join(input.Path, "first.dll"));
        File.Copy(EmbeddedAssembly, Path.Join(input.Path, "second.dll"));

        string? output = EmbeddedPdbExtractor.Extract(input.Path);

        try
        {
            output.Should().NotBeNull();
            Directory.GetFiles(output!, "*.pdb").Select(Path.GetFileName)
                .Should().BeEquivalentTo(["first.pdb", "second.pdb"]);
        }
        finally
        {
            DeleteOutput(output);
        }
    }

    [TestMethod]
    public void Extract_MixedDirectory_WritesOnlyEmbeddedPdbs()
    {
        using TemporaryDirectory input = new();
        File.Copy(EmbeddedAssembly, Path.Join(input.Path, "embedded.dll"));
        File.Copy(PortableAssembly, Path.Join(input.Path, "portable.dll"));
        File.WriteAllText(Path.Join(input.Path, "corrupt.dll"), "not a PE image");
        CopyTruncatedEmbeddedAssembly(Path.Join(input.Path, "truncated.dll"));

        string? output = EmbeddedPdbExtractor.Extract(input.Path);

        try
        {
            output.Should().NotBeNull();
            Directory.GetFiles(output!, "*.pdb").Select(Path.GetFileName)
                .Should().BeEquivalentTo(["embedded.pdb"]);

            Directory.GetFiles(output, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            DeleteOutput(output);
        }
    }

    private static void CopyTruncatedEmbeddedAssembly(string path)
    {
        File.Copy(EmbeddedAssembly, path);
        int embeddedDataOffset;
        using (FileStream stream = File.OpenRead(path))
        using (PEReader reader = new(stream))
        {
            embeddedDataOffset = reader.ReadDebugDirectory()
                .Single(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                .DataPointer;
        }

        using FileStream writeStream = new(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);

        writeStream.SetLength(embeddedDataOffset + 8);
    }

    private static void DeleteOutput(string? output)
    {
        if (output is not null && Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private sealed class TemporaryDirectory : DisposableBase
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                $"filtrace-embedded-pdb-test-{Guid.NewGuid():N}");

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
