// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Text;
using System.Xml;

namespace Filtrace.LocalTesting;

internal sealed class LocalTestingCliInstaller
{
    private readonly TimeSpan _installTimeout;
    private readonly TimeSpan _killGrace;
    private readonly Func<Process, int, bool> _waitForExit;

    public LocalTestingCliInstaller()
        : this(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5), null)
    {
    }

    internal LocalTestingCliInstaller(
        TimeSpan installTimeout,
        TimeSpan killGrace,
        Func<Process, int, bool>? waitForExit = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(installTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(killGrace, TimeSpan.Zero);
        _installTimeout = installTimeout;
        _killGrace = killGrace;
        _waitForExit = waitForExit ?? (static (process, milliseconds) =>
            process.WaitForExit(milliseconds));
    }

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
        string? incompleteOperation = Directory.EnumerateDirectories(
            plan.StateRoot,
            ".cli-install-*",
            SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (incompleteOperation is not null)
        {
            throw new InvalidOperationException(
                $"An incomplete local-testing CLI operation requires manual recovery: '{incompleteOperation}'.");
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
        bool cleanupOperation = true;
        try
        {
            Directory.CreateDirectory(feedDirectory);
            File.Copy(package.Path, feedPackagePath, overwrite: false);
            WriteNuGetConfig(configPath, feedDirectory);
            RunDotnetInstall(
                dotnetPath,
                plan,
                operationRoot,
                configPath,
                package.Version,
                ref cleanupOperation);
            VerifyInstallation(plan.CliDirectory, package);
            return new()
            {
                PackageVersion = package.Version,
                PackageSha256 = package.Sha256
            };
        }
        finally
        {
            if (cleanupOperation && Directory.Exists(operationRoot))
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

    private void RunDotnetInstall(
        string dotnetPath,
        ResourcePlan plan,
        string operationRoot,
        string configPath,
        string version,
        ref bool cleanupOperation)
    {
        ProcessStartInfo startInfo = new(dotnetPath)
        {
            WorkingDirectory = plan.StateRoot,
            UseShellExecute = false
        };
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Join(operationRoot, "dotnet-home");
        startInfo.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Join(operationRoot, "http-cache");
        startInfo.Environment["NUGET_PACKAGES"] = Path.Join(operationRoot, "packages");
        startInfo.Environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Join(operationRoot, "plugins-cache");
        startInfo.Environment["NUGET_SCRATCH"] = Path.Join(operationRoot, "scratch");
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
        if (!_waitForExit(process, (int)_installTimeout.TotalMilliseconds))
        {
            cleanupOperation = false;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or NotSupportedException
                    or System.ComponentModel.Win32Exception)
            {
            }
            bool processExited = _waitForExit(process, (int)_killGrace.TotalMilliseconds);
            File.WriteAllText(
                Path.Join(operationRoot, "installer-process-id"),
                process.Id.ToString());
            if (!processExited)
            {
                throw new InvalidOperationException(
                    $"Could not confirm termination of dotnet process {process.Id}. "
                    + $"The local-testing CLI operation was retained for manual recovery: '{operationRoot}'.");
            }
            throw new TimeoutException(
                $"dotnet tool install did not exit within {_installTimeout.TotalSeconds} seconds. "
                + $"The operation was retained for manual recovery: '{operationRoot}'.");
        }
        if (process.ExitCode is not 0)
        {
            throw new InvalidOperationException(
                $"dotnet tool install exited with code {process.ExitCode}. See dotnet output above.");
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
