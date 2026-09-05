// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.IO.Compression;

namespace Filtrace.LocalTesting.Tests;

internal static class LocalTestingInstallTestData
{
    public static LocalTestingInstallInputs CreateInputs(
        string root,
        string sourceName = "source")
    {
        string source = Path.Join(root, sourceName);
        string packageDirectory = Path.Join(source, "packages");
        string mcpPath = Path.Join(source, "Filtrace.Mcp.dll");
        string skillDirectory = Path.Join(source, "skill");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(mcpPath, "mcp");
        File.WriteAllText(Path.Join(skillDirectory, "SKILL.md"), "skill");
        string packagePath = CreateMetadataPackage(packageDirectory);

        return LocalTestingInstallInputs.Create(
            source,
            packagePath,
            "dotnet",
            mcpPath,
            skillDirectory);
    }

    public static string CreateMetadataPackage(string directory)
    {
        Directory.CreateDirectory(directory);
        string packagePath = Path.Join(directory, "KlutzyNinja.Filtrace.1.2.3.nupkg");
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("package.nuspec");
        using StreamWriter writer = new(entry.Open());
        writer.Write(
            "<package><metadata><id>KlutzyNinja.Filtrace</id><version>1.2.3</version></metadata></package>");

        return packagePath;
    }
}
