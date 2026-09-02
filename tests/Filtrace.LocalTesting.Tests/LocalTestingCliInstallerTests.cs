// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingCliInstallerTests
{
    [TestMethod]
    [DoNotParallelize]
    [Timeout(120_000)]
    public void Install_PackedCli_UsesPrivateDirectoryAndExactPackageBytes()
    {
        using TemporaryDirectory directory = new();
        string packageDirectory = Path.Join(directory.Path, "packages");
        Directory.CreateDirectory(packageDirectory);
        string repositoryRoot = FindRepositoryRoot();
        RunDotnet(
            repositoryRoot,
            "pack",
            Path.Join(repositoryRoot, "src", "Filtrace", "Filtrace.csproj"),
            "--configuration",
            "Release",
            "--no-restore",
            "--output",
            packageDirectory,
            "/p:IncludeSymbols=false");

        string packagePath = Directory.GetFiles(packageDirectory, "*.nupkg").Single();
        string renamedPackagePath = Path.Join(packageDirectory, "renamed.nupkg");
        File.Copy(packagePath, renamedPackagePath);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.StateRoot);

        Action renamedInstall = () => new LocalTestingCliInstaller().InstallFresh(
            plan,
            renamedPackagePath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");

        renamedInstall.Should().Throw<InvalidDataException>()
            .WithMessage("*must be named*");

        Directory.Exists(plan.CliDirectory).Should().BeFalse();
        LocalTestingCliPackage expected = LocalTestingCliPackage.Read(packagePath);
        string ambientPackages = Path.Join(directory.Path, "ambient-packages");
        string ambientPlugins = Path.Join(directory.Path, "ambient-plugins");
        string ambientScratch = Path.Join(directory.Path, "ambient-scratch");
        File.WriteAllText(
            Path.Join(plan.StateRoot, "NuGet.config"),
            "<configuration><packageSources><clear/><add key=\"ambient\" value=\"missing\"/></packageSources></configuration>");

        const string packagesVariable = "NUGET_PACKAGES";
        string? previousPackages = Environment.GetEnvironmentVariable(packagesVariable);
        string? previousPlugins = Environment.GetEnvironmentVariable("NUGET_PLUGINS_CACHE_PATH");
        string? previousScratch = Environment.GetEnvironmentVariable("NUGET_SCRATCH");

        CliInstallation installed;
        try
        {
            Environment.SetEnvironmentVariable(packagesVariable, ambientPackages);
            Environment.SetEnvironmentVariable("NUGET_PLUGINS_CACHE_PATH", ambientPlugins);
            Environment.SetEnvironmentVariable("NUGET_SCRATCH", ambientScratch);
            installed = new LocalTestingCliInstaller().InstallFresh(
                plan,
                packagePath,
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        }
        finally
        {
            Environment.SetEnvironmentVariable(packagesVariable, previousPackages);
            Environment.SetEnvironmentVariable("NUGET_PLUGINS_CACHE_PATH", previousPlugins);
            Environment.SetEnvironmentVariable("NUGET_SCRATCH", previousScratch);
        }

        installed.PackageVersion.Should().Be(expected.Version);
        installed.PackageSha256.Should().Be(expected.Sha256);
        File.Exists(Path.Join(
            plan.CliDirectory,
            OperatingSystem.IsWindows() ? "filtrace.exe" : "filtrace")).Should().BeTrue();

        Directory.Exists(ambientPackages).Should().BeFalse();
        Directory.Exists(ambientPlugins).Should().BeFalse();
        Directory.Exists(ambientScratch).Should().BeFalse();
        Directory.GetDirectories(plan.StateRoot, ".cli-install-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();

        string priorMarker = Path.Join(plan.CliDirectory, "prior-install.txt");
        File.WriteAllText(priorMarker, "replace me");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(priorMarker, FileAttributes.ReadOnly);
        }

        CliInstallation replaced = new LocalTestingCliInstaller().InstallOrReplace(
            plan,
            packagePath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");

        replaced.Should().BeEquivalentTo(installed);
        File.Exists(priorMarker).Should().BeFalse();
        string executablePath = Path.Join(
            plan.CliDirectory,
            OperatingSystem.IsWindows() ? "filtrace.exe" : "filtrace");

        File.Exists(executablePath).Should().BeTrue();
        Directory.GetDirectories(plan.StateRoot, ".cli-install-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [TestMethod]
    public void InstallFresh_MissingDotnet_CleansTemporaryOperation()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreateMetadataPackage(directory.Path);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.StateRoot);

        Action install = () => new LocalTestingCliInstaller().InstallFresh(
            plan,
            packagePath,
            Path.Join(directory.Path, "missing-dotnet"));

        install.Should().Throw<Win32Exception>();
        Directory.Exists(plan.CliDirectory).Should().BeFalse();
        Directory.GetDirectories(plan.StateRoot, ".cli-install-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [TestMethod]
    public void InstallFresh_ExistingCliDirectory_PreservesContents()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreateMetadataPackage(directory.Path);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.CliDirectory);
        string markerPath = Path.Join(plan.CliDirectory, "keep.txt");
        File.WriteAllText(markerPath, "existing");

        Action install = () => new LocalTestingCliInstaller().InstallFresh(
            plan,
            packagePath,
            "dotnet");

        install.Should().Throw<InvalidOperationException>()
            .WithMessage("*CLI path already exists*");

        File.ReadAllText(markerPath).Should().Be("existing");
    }

    [TestMethod]
    [DoNotParallelize]
    public void InstallFresh_ProcessFailure_RemovesOwnedCliAndAllowsRetry()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreateMetadataPackage(directory.Path);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.StateRoot);
        string? previous = Environment.GetEnvironmentVariable(
            LocalTestingCliInstallerProcessProbe.FailureVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                LocalTestingCliInstallerProcessProbe.FailureVariable,
                "1");

            Action install = () => new LocalTestingCliInstaller().InstallFresh(
                plan,
                packagePath,
                GetTestExecutablePath());

            install.Should().Throw<InvalidOperationException>()
                .WithMessage("*exited with code 9*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LocalTestingCliInstallerProcessProbe.FailureVariable,
                previous);
        }

        Directory.Exists(plan.CliDirectory).Should().BeFalse();
        Directory.GetDirectories(plan.StateRoot, ".cli-install-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();

        Action retry = () => new LocalTestingCliInstaller().InstallFresh(
            plan,
            packagePath,
            Path.Join(directory.Path, "missing-dotnet"));

        retry.Should().Throw<Win32Exception>();
    }

    [TestMethod]
    [DoNotParallelize]
    public void InstallOrReplace_ProcessFailure_PreservesExistingCli()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreateMetadataPackage(directory.Path);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.CliDirectory);
        string markerPath = Path.Join(plan.CliDirectory, "keep.txt");
        File.WriteAllText(markerPath, "existing");
        string? previous = Environment.GetEnvironmentVariable(
            LocalTestingCliInstallerProcessProbe.FailureVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                LocalTestingCliInstallerProcessProbe.FailureVariable,
                "1");

            Action install = () => new LocalTestingCliInstaller().InstallOrReplace(
                plan,
                packagePath,
                GetTestExecutablePath());

            install.Should().Throw<InvalidOperationException>()
                .WithMessage("*exited with code 9*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LocalTestingCliInstallerProcessProbe.FailureVariable,
                previous);
        }

        File.ReadAllText(markerPath).Should().Be("existing");
        Directory.GetDirectories(plan.StateRoot, ".cli-install-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [TestMethod]
    [DoNotParallelize]
    public void InstallFresh_ProcessEnvironment_DisablesGlobalToolsPathMutation()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreateMetadataPackage(directory.Path);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.StateRoot);
        string outputPath = Path.Join(directory.Path, "environment.txt");
        const string dotnetVariable = "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH";
        string? previousDotnet = Environment.GetEnvironmentVariable(dotnetVariable);
        string? previousOutput = Environment.GetEnvironmentVariable(
            LocalTestingCliInstallerProcessProbe.EnvironmentOutputVariable);

        try
        {
            Environment.SetEnvironmentVariable(dotnetVariable, "true");
            Environment.SetEnvironmentVariable(
                LocalTestingCliInstallerProcessProbe.EnvironmentOutputVariable,
                outputPath);

            Action install = () => new LocalTestingCliInstaller().InstallFresh(
                plan,
                packagePath,
                GetTestExecutablePath());

            install.Should().Throw<InvalidOperationException>()
                .WithMessage("*exited with code 9*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(dotnetVariable, previousDotnet);
            Environment.SetEnvironmentVariable(
                LocalTestingCliInstallerProcessProbe.EnvironmentOutputVariable,
                previousOutput);
        }

        File.ReadAllText(outputPath).Should().Be("false");
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(10_000)]
    public void InstallFresh_ProcessTimeout_IsEndToEndBoundedAndQuarantinesOperation()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreateMetadataPackage(directory.Path);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.StateRoot);
        const string variable = "FILTRACE_LOCAL_TESTING_CLI_INSTALLER_TIMEOUT_PROBE";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            Stopwatch stopwatch = Stopwatch.StartNew();

            Action install = () => new LocalTestingCliInstaller(
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(2)).InstallFresh(
                    plan,
                    packagePath,
                    GetTestExecutablePath());

            install.Should().Throw<TimeoutException>();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }

        string operationRoot = Directory.GetDirectories(
            plan.StateRoot,
            ".cli-install-*",
            SearchOption.TopDirectoryOnly).Single();

        File.Exists(Path.Join(operationRoot, "installer-process-id")).Should().BeTrue();
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(10_000)]
    public void InstallOrReplace_ProcessTimeout_PreservesExistingCliAndQuarantinesOperation()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreateMetadataPackage(directory.Path);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.CliDirectory);
        string markerPath = Path.Join(plan.CliDirectory, "keep.txt");
        File.WriteAllText(markerPath, "existing");
        const string variable = "FILTRACE_LOCAL_TESTING_CLI_INSTALLER_TIMEOUT_PROBE";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            Action install = () => new LocalTestingCliInstaller(
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(2)).InstallOrReplace(
                    plan,
                    packagePath,
                    GetTestExecutablePath());

            install.Should().Throw<TimeoutException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }

        File.ReadAllText(markerPath).Should().Be("existing");
        string operationRoot = Directory.GetDirectories(
            plan.StateRoot,
            ".cli-install-*",
            SearchOption.TopDirectoryOnly).Single();

        File.Exists(Path.Join(operationRoot, "installer-process-id")).Should().BeTrue();
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(10_000)]
    public void InstallFresh_UnconfirmedTermination_RetainsOperationAndBlocksRetry()
    {
        using TemporaryDirectory directory = new();
        string packagePath = CreateMetadataPackage(directory.Path);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.StateRoot);
        const string variable = "FILTRACE_LOCAL_TESTING_CLI_INSTALLER_TIMEOUT_PROBE";
        string? previous = Environment.GetEnvironmentVariable(variable);
        int waitCount = 0;
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            LocalTestingCliInstaller installer = new(
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(2),
                (process, milliseconds) =>
                {
                    bool exited = process.WaitForExit(milliseconds);
                    return ++waitCount is 1 ? exited : false;
                });

            Action install = () => installer.InstallFresh(
                plan,
                packagePath,
                GetTestExecutablePath());

            install.Should().Throw<InvalidOperationException>()
                .WithMessage("*Could not confirm termination*retained for manual recovery*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }

        string operationRoot = Directory.GetDirectories(
            plan.StateRoot,
            ".cli-install-*",
            SearchOption.TopDirectoryOnly).Single();

        File.Exists(Path.Join(operationRoot, "installer-process-id")).Should().BeTrue();
        Action retry = () => new LocalTestingCliInstaller().InstallFresh(plan, packagePath, "dotnet");
        retry.Should().Throw<InvalidOperationException>()
            .WithMessage("*incomplete local-testing CLI operation requires manual recovery*");
    }

    private static ResourcePlan CreatePlan(string root)
    {
        string targetRoot = Path.Join(root, "target");
        string gitDirectory = Path.Join(root, "git");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(gitDirectory);
        return ResourcePlan.Create(targetRoot, gitDirectory);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "filtrace.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find the Filtrace repository root.");
    }

    private static string GetTestExecutablePath()
    {
        string assemblyName = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location);
        return Path.Join(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? $"{assemblyName}.exe" : assemblyName);
    }

    private static string CreateMetadataPackage(string directory)
    {
        string packagePath = Path.Join(directory, "KlutzyNinja.Filtrace.1.2.3.nupkg");
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("package.nuspec");
        using StreamWriter writer = new(entry.Open());
        writer.Write(
            "<package><metadata><id>KlutzyNinja.Filtrace</id><version>1.2.3</version></metadata></package>");

        return packagePath;
    }

    private static void RunDotnet(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet.");

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("dotnet did not exit within 90 seconds.");
        }

        Task.WaitAll(output, error);
        process.ExitCode.Should().Be(0, $"{error.Result}\n{output.Result}");
    }
}
