// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.IO.Compression;
using System.Security.Cryptography;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingCliPackageTests
{
    [TestMethod]
    public void Read_ValidPackage_ReturnsExactIdentityAndHash()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreatePackage(directory.Path, LocalTestingCliPackage.PackageId, "1.2.3");
        string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)))
            .ToLowerInvariant();

        LocalTestingCliPackage package = LocalTestingCliPackage.Read(packagePath);

        package.Path.Should().Be(Path.GetFullPath(packagePath));
        package.Version.Should().Be("1.2.3");
        package.Sha256.Should().Be(expectedHash);
    }

    [TestMethod]
    public void Read_WrongPackageId_Throws()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreatePackage(directory.Path, "Other.Tool", "1.2.3");

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*Other.Tool*expected*KlutzyNinja.Filtrace*");
    }

    [TestMethod]
    public void Read_MultipleRootNuspecs_Throws()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreatePackage(directory.Path, LocalTestingCliPackage.PackageId, "1.2.3");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            WriteNuspec(archive, "second.nuspec", LocalTestingCliPackage.PackageId, "1.2.3");
        }

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*exactly one root nuspec; found 2*");
    }

    [TestMethod]
    public void Read_NonPackageExtension_Throws()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Join(directory.Path, "package.zip");
        File.WriteAllText(packagePath, "not a package");

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*must have a .nupkg extension*");
    }

    [TestMethod]
    public void Read_OversizedNuspec_Throws()
    {
        using TemporaryDirectory directory = new();
        string packagePath = Path.Join(directory.Path, "package.nupkg");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("package.nuspec");
            using Stream stream = entry.Open();
            stream.Write(new byte[(1024 * 1024) + 1]);
        }

        Action read = () => LocalTestingCliPackage.Read(packagePath);

        read.Should().Throw<InvalidDataException>()
            .WithMessage("*nuspec exceeds the 1048576 byte safety limit*");
    }

    private static string CreatePackage(string directory, string id, string version)
    {
        string packagePath = Path.Join(directory, "package.nupkg");
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteNuspec(archive, "package.nuspec", id, version);
        return packagePath;
    }

    private static void WriteNuspec(ZipArchive archive, string name, string id, string version)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open());
        writer.Write($"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
    }
}