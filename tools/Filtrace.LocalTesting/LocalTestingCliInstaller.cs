// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using System.Text;
using System.Xml;

namespace Filtrace.LocalTesting;

/// <summary>
///  Installs the Filtrace CLI into an isolated tool path and verifies the installed package bytes.
/// </summary>
internal sealed class LocalTestingCliInstaller
{
    private readonly TimeSpan _installTimeout;
    private readonly TimeSpan _killGrace;
    private readonly Func<Process, int, bool> _waitForExit;
    private readonly Action? _beforePublish;
    private readonly Action? _beforeRollback;

    /// <summary>
    ///  Creates an installer with a 90-second process timeout and a five-second termination grace period.
    /// </summary>
    public LocalTestingCliInstaller()
        : this(
            installTimeout: TimeSpan.FromSeconds(90),
            killGrace: TimeSpan.FromSeconds(5),
            waitForExit: null,
            beforePublish: null,
            beforeRollback: null)
    {
    }

    /// <summary>
    ///  Creates an installer with bounded, testable process-lifetime behavior.
    /// </summary>
    /// <param name="installTimeout">The maximum duration allowed for <c>dotnet tool install</c>.</param>
    /// <param name="killGrace">The time allowed to observe process exit after termination is requested.</param>
    /// <param name="waitForExit">An optional process wait implementation used to test timeout recovery.</param>
    /// <param name="beforePublish">An optional hook invoked before the staged CLI is published.</param>
    /// <param name="beforeRollback">An optional hook invoked before a prior CLI is restored.</param>
    internal LocalTestingCliInstaller(
        TimeSpan installTimeout,
        TimeSpan killGrace,
        Func<Process, int, bool>? waitForExit = null,
        Action? beforePublish = null,
        Action? beforeRollback = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(installTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(killGrace, TimeSpan.Zero);
        _installTimeout = installTimeout;
        _killGrace = killGrace;
        _waitForExit = waitForExit ?? (static (process, milliseconds) =>
        {
            return process.WaitForExit(milliseconds);
        });

        _beforePublish = beforePublish;
        _beforeRollback = beforeRollback;
    }

    /// <summary>
    ///  Installs one validated package using private NuGet caches, then verifies its executable and tool-store package.
    /// </summary>
    /// <param name="plan">The target's normalized local-testing resource paths.</param>
    /// <param name="packagePath">The canonically named Filtrace CLI package to install.</param>
    /// <param name="dotnetPath">The <c>dotnet</c> host path or command name.</param>
    /// <returns>The exact version and SHA-256 identity of the installed package.</returns>
    public CliInstallation InstallFresh(ResourcePlan plan, string packagePath, string dotnetPath)
    {
        return Install(plan, packagePath, dotnetPath, replaceExisting: false);
    }

    /// <summary>
    ///  Installs one validated package and atomically replaces the CLI owned by existing local-testing state.
    /// </summary>
    /// <param name="plan">The target's normalized local-testing resource paths.</param>
    /// <param name="packagePath">The canonically named Filtrace CLI package to install.</param>
    /// <param name="dotnetPath">The <c>dotnet</c> host path or command name.</param>
    /// <returns>The exact version and SHA-256 identity of the installed package.</returns>
    public CliInstallation InstallOrReplace(
        ResourcePlan plan,
        string packagePath,
        string dotnetPath)
    {
        return Install(plan, packagePath, dotnetPath, replaceExisting: true);
    }

    /// <summary>
    ///  Removes the private CLI unless an incomplete installation remains quarantined for manual recovery.
    /// </summary>
    /// <param name="plan">The target's normalized local-testing resource paths.</param>
    public void Restore(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfIncompleteOperationExists(plan);
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.CliDirectory);
        if (RegularFileGuard.Exists(plan.CliDirectory, "Local-testing CLI path"))
        {
            throw new InvalidDataException(
                $"Local-testing CLI path is a file, not a directory: '{plan.CliDirectory}'.");
        }

        if (Directory.Exists(plan.CliDirectory))
        {
            LocalTestingDirectory.DeleteTree(plan.CliDirectory);
        }
    }

    private CliInstallation Install(
        ResourcePlan plan,
        string packagePath,
        string dotnetPath,
        bool replaceExisting)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);
        LocalTestingCliPackage package = LocalTestingCliPackage.Read(packagePath);
        if (!Directory.Exists(plan.StateRoot))
        {
            throw new DirectoryNotFoundException(
                $"Local-testing state directory does not exist: '{plan.StateRoot}'.");
        }

        ThrowIfIncompleteOperationExists(plan);

        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.CliDirectory);
        bool destinationExists = Directory.Exists(plan.CliDirectory);
        if (RegularFileGuard.Exists(plan.CliDirectory, "Local-testing CLI path"))
        {
            throw new InvalidDataException(
                $"Local-testing CLI path is a file, not a directory: '{plan.CliDirectory}'.");
        }

        if (destinationExists && !replaceExisting)
        {
            throw new InvalidOperationException(
                $"Local-testing CLI path already exists: '{plan.CliDirectory}'.");
        }

        string operationRoot = Path.Join(plan.StateRoot, $".cli-install-{Guid.NewGuid():N}");
        string feedDirectory = Path.Join(operationRoot, "feed");
        string feedPackagePath = Path.Join(feedDirectory, Path.GetFileName(package.Path));
        string configPath = Path.Join(operationRoot, "NuGet.config");
        string installationDirectory = Path.Join(operationRoot, "tools");
        string retiredDirectory = Path.Join(operationRoot, "retired-tools");
        bool cleanupOperation = true;
        try
        {
            Directory.CreateDirectory(feedDirectory);
            File.Copy(package.Path, feedPackagePath, overwrite: false);
            WriteNuGetConfig(configPath, feedDirectory);
            RunDotnetInstall(
                dotnetPath,
                plan,
                installationDirectory,
                operationRoot,
                configPath,
                package.Version,
                ref cleanupOperation);

            VerifyInstallation(installationDirectory, package);
            PublishInstallation(
                plan,
                installationDirectory,
                retiredDirectory,
                replaceExisting);

            return new()
            {
                PackageVersion = package.Version,
                PackageSha256 = package.Sha256
            };
        }
        finally
        {
            if (cleanupOperation
                && Directory.Exists(operationRoot)
                && !Directory.Exists(retiredDirectory))
            {
                LocalTestingDirectory.DeleteTree(operationRoot);
            }
        }
    }

    private static void ThrowIfIncompleteOperationExists(ResourcePlan plan)
    {
        string? incompleteOperation = Directory.EnumerateDirectories(
            plan.StateRoot,
            ".cli-install-*",
            SearchOption.TopDirectoryOnly).FirstOrDefault();

        if (incompleteOperation is not null)
        {
            throw new InvalidOperationException(
                $"An incomplete local-testing CLI operation requires manual recovery: '{incompleteOperation}'.");
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
        string installationDirectory,
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

        startInfo.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Join(operationRoot, "dotnet-home");
        startInfo.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true";
        startInfo.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Join(operationRoot, "http-cache");
        startInfo.Environment["NUGET_PACKAGES"] = Path.Join(operationRoot, "packages");
        startInfo.Environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Join(operationRoot, "plugins-cache");
        startInfo.Environment["NUGET_SCRATCH"] = Path.Join(operationRoot, "scratch");
        string[] arguments =
        [
            "tool", "install", "--tool-path", installationDirectory,
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

    private void PublishInstallation(
        ResourcePlan plan,
        string installationDirectory,
        string retiredDirectory,
        bool replaceExisting)
    {
        ManagedPathGuard.EnsureNoLinks(plan.GitDirectory, plan.CliDirectory);
        bool destinationExists = Directory.Exists(plan.CliDirectory);
        if (RegularFileGuard.Exists(plan.CliDirectory, "Local-testing CLI path"))
        {
            throw new InvalidDataException(
                $"Local-testing CLI path is a file, not a directory: '{plan.CliDirectory}'.");
        }

        if (destinationExists && !replaceExisting)
        {
            throw new InvalidOperationException(
                $"Local-testing CLI path already exists: '{plan.CliDirectory}'.");
        }

        if (destinationExists)
        {
            Directory.Move(plan.CliDirectory, retiredDirectory);
        }

        try
        {
            _beforePublish?.Invoke();
            Directory.Move(installationDirectory, plan.CliDirectory);
        }
        catch (Exception publishException)
        {
            if (!destinationExists)
            {
                throw;
            }

            try
            {
                if (Directory.Exists(plan.CliDirectory))
                {
                    throw new IOException(
                        $"The local-testing CLI path reappeared before rollback: '{plan.CliDirectory}'.");
                }

                _beforeRollback?.Invoke();
                Directory.Move(retiredDirectory, plan.CliDirectory);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "Could not publish the staged local-testing CLI or restore the prior CLI. "
                        + $"The prior CLI and operation were retained for manual recovery: '{retiredDirectory}'.",
                    new AggregateException(publishException, rollbackException));
            }

            throw;
        }

        if (destinationExists)
        {
            try
            {
                LocalTestingDirectory.DeleteTree(retiredDirectory);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "The new local-testing CLI was published, but the prior CLI could not be removed. "
                        + $"The operation was retained for manual recovery: '{retiredDirectory}'.",
                    exception);
            }
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
                .Select(LocalTestingCliPackage.ReadInstalled)
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
