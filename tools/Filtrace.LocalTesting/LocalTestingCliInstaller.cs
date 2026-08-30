// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Text;
using System.Xml;

namespace Filtrace.LocalTesting;

internal sealed class LocalTestingCliInstaller
{
    private static readonly TimeSpan s_installTimeout = TimeSpan.FromSeconds(90);

    public CliInstallation InstallFresh(ResourcePlan plan, string packagePath, string dotnetPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);
        LocalTestingCliPackage package = LocalTestingCliPackage.Read(packagePath);
        if (!Directory.Exists(plan.StateRoot))
        {
            throw new DirectoryNotFoundException(
                $"Local-testing state directory does not exist: '{plan.StateRoot}'.");
        }

        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.CliDirectory);
        if (Directory.Exists(plan.CliDirectory)
            || RegularFileGuard.Exists(plan.CliDirectory, "Local-testing CLI path"))
        {
            throw new InvalidOperationException(
                $"Local-testing CLI path already exists: '{plan.CliDirectory}'.");
        }

        string operationRoot = Path.Join(plan.StateRoot, $".cli-install-{Guid.NewGuid():N}");
        string feedDirectory = Path.Join(operationRoot, "feed");
        string feedPackagePath = Path.Join(feedDirectory, Path.GetFileName(package.Path));
        string configPath = Path.Join(operationRoot, "NuGet.config");
        Directory.CreateDirectory(feedDirectory);
        try
        {
            File.Copy(package.Path, feedPackagePath, overwrite: false);
            WriteNuGetConfig(configPath, feedDirectory);
            RunDotnetInstall(
                dotnetPath,
                plan,
                operationRoot,
                configPath,
                package.Version);
            VerifyInstallation(plan.CliDirectory, package);
            return new()
            {
                PackageVersion = package.Version,
                PackageSha256 = package.Sha256
            };
        }
        finally
        {
            if (Directory.Exists(operationRoot))
            {
                Directory.Delete(operationRoot, recursive: true);
            }
        }
    }

    private static void WriteNuGetConfig(string path, string feedDirectory)
    {
        XmlWriterSettings settings = new()
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true
        };
        using XmlWriter writer = XmlWriter.Create(path, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("configuration");
        writer.WriteStartElement("packageSources");
        writer.WriteStartElement("clear");
        writer.WriteEndElement();
        writer.WriteStartElement("add");
        writer.WriteAttributeString("key", "local-filtrace");
        writer.WriteAttributeString("value", feedDirectory);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void RunDotnetInstall(
        string dotnetPath,
        ResourcePlan plan,
        string operationRoot,
        string configPath,
        string version)
    {
        ProcessStartInfo startInfo = new(dotnetPath)
        {
            WorkingDirectory = plan.StateRoot,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Join(operationRoot, "dotnet-home");
        startInfo.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Join(operationRoot, "http-cache");
        startInfo.Environment["NUGET_PACKAGES"] = Path.Join(operationRoot, "packages");
        string[] arguments =
        [
            "tool", "install", "--tool-path", plan.CliDirectory,
            "--configfile", configPath, "--version", version, "--no-cache",
            LocalTestingCliPackage.PackageId
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet tool install.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)s_installTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WaitAll(output, error);
            throw new TimeoutException(
                $"dotnet tool install did not exit within {s_installTimeout.TotalSeconds} seconds.");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode is not 0)
        {
            string diagnostics = string.Join(
                Environment.NewLine,
                new[] { error.Result.Trim(), output.Result.Trim() }.Where(text => text.Length > 0));
            throw new InvalidOperationException(
                $"dotnet tool install exited with code {process.ExitCode}: {diagnostics}");
        }
    }

    private static void VerifyInstallation(string cliDirectory, LocalTestingCliPackage expected)
    {
        string executablePath = Path.Join(
            cliDirectory,
            OperatingSystem.IsWindows() ? "filtrace.exe" : "filtrace");
        if (!RegularFileGuard.Exists(executablePath, "Installed Filtrace CLI"))
        {
            throw new InvalidDataException(
                $"dotnet tool install did not create the Filtrace CLI: '{executablePath}'.");
        }

        string storeDirectory = Path.Join(cliDirectory, ".store");
        if (!Directory.Exists(storeDirectory))
        {
            throw new InvalidDataException(
                $"dotnet tool install did not create the tool store: '{storeDirectory}'.");
        }

        LocalTestingCliPackage[] installed =
        [
            .. Directory.EnumerateFiles(storeDirectory, "*.nupkg", SearchOption.AllDirectories)
                .Select(LocalTestingCliPackage.Read)
        ];
        if (installed.Length is 0
            || installed.Any(package =>
                !package.Version.Equals(expected.Version, StringComparison.Ordinal)
                || !package.Sha256.Equals(expected.Sha256, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Installed Filtrace CLI package does not match the prepared package bytes.");
        }
    }
}