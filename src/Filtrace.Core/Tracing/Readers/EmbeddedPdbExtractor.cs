// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Extracts embedded portable PDBs (the <c>&lt;DebugType&gt;embedded&lt;/DebugType&gt;</c>
///  stream baked into a managed assembly for SourceLink) out of the DLLs in a
///  build-output directory and writes them as standalone <c>.pdb</c> files into a
///  temporary directory.
/// </summary>
/// <remarks>
///  <para>
///   TraceEvent's <c>SymbolReader</c> resolves source lines only from standalone
///   <c>.pdb</c> files - it never reads a PDB embedded in the PE image. Libraries
///   such as touki ship <c>embedded</c> PDBs, and BenchmarkDotNet runs each
///   benchmark from an ephemeral build directory that is deleted before the trace
///   is analyzed, so no standalone PDB is ever locatable by the recorded module
///   path. Re-materializing the embedded PDB next to a copy on disk and pointing
///   the symbol reader at it lets TraceEvent match the module by its PDB GUID and
///   resolve <c>file:line</c> for the managed frames.
///  </para>
///  <para>
///   The upstream follow-up that would retire this workaround - TraceEvent reading
///   embedded PDBs directly - is TE-P1 in <c>docs/roadmap.md</c>.
///  </para>
/// </remarks>
internal static class EmbeddedPdbExtractor
{
    // 'M' 'P' 'D' 'B' little-endian: the signature of an embedded portable PDB blob.
    private const uint EmbeddedPdbMagic = 0x4244504D;
    private const long MaximumExtractedBytes = 256L * 1024 * 1024;
    private const int CopyBufferSize = 81920;

    /// <summary>
    ///  Extracts every embedded portable PDB found in the DLLs of
    ///  <paramref name="buildOutputDirectory"/> into a fresh temporary directory.
    /// </summary>
    /// <param name="buildOutputDirectory">A directory containing built managed assemblies.</param>
    /// <returns>
    ///  The temporary directory holding the extracted standalone PDBs, or
    ///  <see langword="null"/> when the directory does not exist or contains no
    ///  embedded PDBs. The caller owns the returned directory and should delete it
    ///  when finished.
    /// </returns>
    public static string? Extract(string buildOutputDirectory)
    {
        long remainingExtractedBytes = MaximumExtractedBytes;
        return ExtractCore(buildOutputDirectory, ref remainingExtractedBytes);
    }

    private static string? ExtractCore(
        string buildOutputDirectory,
        ref long remainingExtractedBytes)
    {
        if (string.IsNullOrEmpty(buildOutputDirectory) || !Directory.Exists(buildOutputDirectory))
        {
            return null;
        }

        string? tempDirectory = null;

        foreach (string dll in Directory.EnumerateFiles(buildOutputDirectory, "*.dll"))
        {
            string? temporaryPdbPath = null;
            try
            {
                byte[] image = File.ReadAllBytes(dll);
                using MemoryStream peStream = new(image, writable: false);
                using PEReader peReader = new(peStream);

                foreach (DebugDirectoryEntry entry in peReader.ReadDebugDirectory())
                {
                    if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
                    {
                        continue;
                    }

                    int dataOffset = entry.DataPointer;
                    if (dataOffset < 0 || dataOffset + 8 > image.Length || entry.DataSize <= 8)
                    {
                        continue;
                    }

                    if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(dataOffset, 4)) != EmbeddedPdbMagic)
                    {
                        continue;
                    }

                    uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                        image.AsSpan(dataOffset + 4, 4));
                    if (uncompressedSize == 0 || uncompressedSize > remainingExtractedBytes)
                    {
                        continue;
                    }

                    remainingExtractedBytes -= uncompressedSize;

                    tempDirectory ??= CreateTempDirectory();
                    string pdbPath = Path.Join(
                        tempDirectory,
                        Path.GetFileNameWithoutExtension(dll) + ".pdb");
                    temporaryPdbPath = $"{pdbPath}.{Guid.NewGuid():N}.tmp";

                    // Layout of the embedded blob: 4-byte 'MPDB' magic, 4-byte
                    // uncompressed size, then the portable PDB as a raw deflate stream.
                    using (MemoryStream compressed = new(
                        image,
                        dataOffset + 8,
                        entry.DataSize - 8,
                        writable: false))
                    using (DeflateStream deflate = new(compressed, CompressionMode.Decompress))
                    using (FileStream outPdb = File.Create(temporaryPdbPath))
                    {
                        CopyExactly(deflate, outPdb, uncompressedSize);
                    }

                    File.Move(temporaryPdbPath, pdbPath, overwrite: true);
                    temporaryPdbPath = null;
                }
            }
            catch (Exception)
            {
                // Extraction is best-effort: a DLL that cannot be read or whose
                // embedded PDB cannot be decompressed simply contributes no symbols.
                if (temporaryPdbPath is not null)
                {
                    TryDelete(temporaryPdbPath);
                }

                if (tempDirectory is not null && TryDeleteEmptyDirectory(tempDirectory))
                {
                    tempDirectory = null;
                }
            }
        }

        return tempDirectory;
    }

    private static void CopyExactly(Stream source, Stream destination, long expectedBytes)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long remainingBytes = expectedBytes;
            while (remainingBytes > 0)
            {
                int bytesRead = source.Read(
                    buffer.AsSpan(0, (int)Math.Min(buffer.Length, remainingBytes)));
                if (bytesRead == 0)
                {
                    throw new InvalidDataException(
                        "Embedded PDB is shorter than its declared uncompressed size.");
                }

                destination.Write(buffer.AsSpan(0, bytesRead));
                remainingBytes -= bytesRead;
            }

            if (source.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    "Embedded PDB is longer than its declared uncompressed size.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Join(Path.GetTempPath(), $"filtrace-pdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Preserve best-effort extraction when cleanup itself loses a race.
        }
    }

    private static bool TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                return false;
            }

            Directory.Delete(path);
            return true;
        }
        catch (Exception)
        {
            // Preserve best-effort extraction when cleanup itself loses a race.
            return false;
        }
    }
}
