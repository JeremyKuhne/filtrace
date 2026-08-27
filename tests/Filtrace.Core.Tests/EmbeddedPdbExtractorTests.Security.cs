// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.PortableExecutable;
using Filtrace.Tracing.Readers;

namespace Filtrace.Core.Tests;

public sealed partial class EmbeddedPdbExtractorTests
{
    private delegate string? ExtractCoreDelegate(
        string buildOutputDirectory,
        ref long remainingExtractedBytes);

    private static readonly ExtractCoreDelegate ExtractCore =
        typeof(EmbeddedPdbExtractor).GetMethod(
            "ExtractCore",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(long).MakeByRefType()],
            modifiers: null)!.CreateDelegate<ExtractCoreDelegate>();

    [TestMethod]
    public void Extract_AggregateSizeAtBudget_WritesEveryPdb()
    {
        using TemporaryDirectory input = new();
        File.Copy(EmbeddedAssembly, Path.Join(input.Path, "first.dll"));
        File.Copy(EmbeddedAssembly, Path.Join(input.Path, "second.dll"));
        int declaredSize = GetDeclaredSize(EmbeddedAssembly);
        long remainingExtractedBytes = (long)declaredSize * 2;

        string? output = ExtractCore(input.Path, ref remainingExtractedBytes);

        try
        {
            output.Should().NotBeNull();
            Directory.GetFiles(output!, "*.pdb").Should().HaveCount(2);
            remainingExtractedBytes.Should().Be(0);
        }
        finally
        {
            DeleteOutput(output);
        }
    }

    [TestMethod]
    public void Extract_AggregateSizeOverBudget_SkipsPdbBeyondBudget()
    {
        using TemporaryDirectory input = new();
        File.Copy(EmbeddedAssembly, Path.Join(input.Path, "first.dll"));
        File.Copy(EmbeddedAssembly, Path.Join(input.Path, "second.dll"));
        int declaredSize = GetDeclaredSize(EmbeddedAssembly);
        long remainingExtractedBytes = (long)declaredSize * 2 - 1;

        string? output = ExtractCore(input.Path, ref remainingExtractedBytes);

        try
        {
            output.Should().NotBeNull();
            Directory.GetFiles(output!, "*.pdb").Should().ContainSingle();
            remainingExtractedBytes.Should().Be(declaredSize - 1);
        }
        finally
        {
            DeleteOutput(output);
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public void Extract_DeclaredSizeDoesNotMatchOutput_ReturnsNull(int sizeDelta)
    {
        using TemporaryDirectory input = new();
        string assembly = Path.Join(input.Path, "mismatched.dll");
        File.Copy(EmbeddedAssembly, assembly);
        int declaredSize = GetDeclaredSize(assembly);
        SetDeclaredSize(assembly, checked((uint)(declaredSize + sizeDelta)));

        string? output = EmbeddedPdbExtractor.Extract(input.Path);

        try
        {
            output.Should().BeNull();
        }
        finally
        {
            DeleteOutput(output);
        }
    }

    [TestMethod]
    public void Extract_FailedExtraction_ReservesDeclaredSize()
    {
        using TemporaryDirectory input = new();
        string assembly = Path.Join(input.Path, "mismatched.dll");
        File.Copy(EmbeddedAssembly, assembly);
        const uint declaredSize = 128u * 1024 * 1024;
        SetDeclaredSize(assembly, declaredSize);
        long remainingExtractedBytes = 256L * 1024 * 1024;

        string? output = ExtractCore(input.Path, ref remainingExtractedBytes);

        try
        {
            output.Should().BeNull();
            remainingExtractedBytes.Should().Be(128L * 1024 * 1024);
        }
        finally
        {
            DeleteOutput(output);
        }
    }

    [TestMethod]
    public void Extract_DefaultAggregateBudget_Is256MiB()
    {
        FieldInfo field = typeof(EmbeddedPdbExtractor).GetField(
            "MaximumExtractedBytes",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        field.GetRawConstantValue().Should().Be(256L * 1024 * 1024);
    }

    private static int GetDeclaredSize(string assembly)
    {
        byte[] image = File.ReadAllBytes(assembly);
        DebugDirectoryEntry entry = GetEmbeddedPdbEntry(assembly);
        return BinaryPrimitives.ReadInt32LittleEndian(
            image.AsSpan(entry.DataPointer + 4, sizeof(int)));
    }

    private static void SetDeclaredSize(string assembly, uint declaredSize)
    {
        DebugDirectoryEntry entry = GetEmbeddedPdbEntry(assembly);
        using FileStream stream = new(
            assembly,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);
        stream.Position = entry.DataPointer + 4;
        Span<byte> encodedSize = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(encodedSize, declaredSize);
        stream.Write(encodedSize);
    }

    private static DebugDirectoryEntry GetEmbeddedPdbEntry(string assembly)
    {
        using FileStream stream = File.OpenRead(assembly);
        using PEReader reader = new(stream);
        return reader.ReadDebugDirectory()
            .Single(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
    }
}
