// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Filtrace.LocalTesting;

internal sealed record LocalTestingCliPackage(string Path, string Version, string Sha256)
{
    public const string PackageId = "KlutzyNinja.Filtrace";
    internal const int MaxArchiveEntries = 1024;
    internal const long MaxExpandedBytes = 128L * 1024 * 1024;
    internal const long MaxPackageBytes = 32L * 1024 * 1024;
    private const int MaxNuspecBytes = 1024 * 1024;

    public static LocalTestingCliPackage Read(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        string fullPath = System.IO.Path.GetFullPath(packagePath);
        if (!System.IO.Path.GetExtension(fullPath).Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"CLI package must have a .nupkg extension: '{fullPath}'.");
        }
        if (!RegularFileGuard.Exists(fullPath, "CLI package"))
        {
            throw new FileNotFoundException("CLI package does not exist.", fullPath);
        }

        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidatePackageLength(stream.Length, fullPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ValidateArchiveLimits(
            archive.Entries.Count,
            archive.Entries.Select(entry => entry.Length),
            fullPath);
        ZipArchiveEntry[] nuspecs =
        [
            .. archive.Entries.Where(entry =>
                entry.Name.Equals(entry.FullName, StringComparison.Ordinal)
                && entry.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        ];
        if (nuspecs.Length is not 1)
        {
            throw new InvalidDataException(
                $"CLI package must contain exactly one root nuspec; found {nuspecs.Length}: '{fullPath}'.");
        }

        (string id, string version) = ReadIdentity(nuspecs[0], fullPath);
        if (!id.Equals(PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"CLI package id is '{id}'; expected '{PackageId}': '{fullPath}'.");
        }

        return new(fullPath, version, sha256);
    }

    private static (string Id, string Version) ReadIdentity(
        ZipArchiveEntry nuspec,
        string packagePath)
    {
        if (nuspec.Length > MaxNuspecBytes)
        {
            throw new InvalidDataException(
                $"CLI package nuspec exceeds the {MaxNuspecBytes} byte safety limit: '{packagePath}'.");
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxNuspecBytes,
            XmlResolver = null
        };
        using Stream stream = nuspec.Open();
        using XmlReader reader = XmlReader.Create(stream, settings);
        XDocument document;
        try
        {
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"CLI package nuspec is not valid XML: '{packagePath}'.",
                exception);
        }
        XElement package = document.Root is { Name.LocalName: "package" } root
            ? root
            : throw new InvalidDataException($"CLI package nuspec has no package root: '{packagePath}'.");
        XElement metadata = ReadSingleElement(package, "metadata", packagePath);
        string id = ReadSingleElement(metadata, "id", packagePath).Value.Trim();
        string version = ReadSingleElement(metadata, "version", packagePath).Value.Trim();
        if (id.Length is 0 || version.Length is 0)
        {
            throw new InvalidDataException($"CLI package nuspec identity is incomplete: '{packagePath}'.");
        }

        return (id, version);
    }

    internal static void ValidatePackageLength(long length, string packagePath)
    {
        if (length > MaxPackageBytes)
        {
            throw new InvalidDataException(
                $"CLI package exceeds the {MaxPackageBytes} byte safety limit: '{packagePath}'.");
        }
    }

    internal static void ValidateArchiveLimits(
        int entryCount,
        IEnumerable<long> entryLengths,
        string packagePath)
    {
        if (entryCount > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"CLI package exceeds the {MaxArchiveEntries} entry safety limit: '{packagePath}'.");
        }

        long totalLength = 0;
        try
        {
            foreach (long length in entryLengths)
            {
                totalLength = checked(totalLength + length);
                if (totalLength > MaxExpandedBytes)
                {
                    throw new InvalidDataException(
                        $"CLI package expands beyond the {MaxExpandedBytes} byte safety limit: '{packagePath}'.");
                }
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"CLI package expanded size is invalid: '{packagePath}'.",
                exception);
        }
    }

    private static XElement ReadSingleElement(XElement parent, string name, string packagePath)
    {
        XElement[] matches = [.. parent.Elements().Where(
            element => element.Name.LocalName.Equals(name, StringComparison.Ordinal))];
        return matches.Length is 1
            ? matches[0]
            : throw new InvalidDataException(
                $"CLI package nuspec must contain exactly one '{name}' element: '{packagePath}'.");
    }
}
