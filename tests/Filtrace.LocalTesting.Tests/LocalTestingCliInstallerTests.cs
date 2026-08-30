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
        LocalTestingCliPackage expected = LocalTestingCliPackage.Read(packagePath);
        ResourcePlan plan = CreatePlan(directory.Path);
        Directory.CreateDirectory(plan.StateRoot);
        string ambientPackages = Path.Join(directory.Path, "ambient-packages");
        File.WriteAllText(
            Path.Join(plan.StateRoot, "NuGet.config"),
            "<configuration><packageSources><clear/><add key=\"ambient\" value=\"missing\"/></packageSources></configuration>");
        const string packagesVariable = "NUGET_PACKAGES";
        string? previousPackages = Environment.GetEnvironmentVariable(packagesVariable);

        CliInstallation installed;
        try
        {
            Environment.SetEnvironmentVariable(packagesVariable, ambientPackages);
            installed = new LocalTestingCliInstaller().InstallFresh(
                plan,
                packagePath,
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        }
        finally
        {
            Environment.SetEnvironmentVariable(packagesVariable, previousPackages);
        }

        installed.PackageVersion.Should().Be(expected.Version);
        installed.PackageSha256.Should().Be(expected.Sha256);
        File.Exists(Path.Join(
            plan.CliDirectory,
            OperatingSystem.IsWindows() ? "filtrace.exe" : "filtrace")).Should().BeTrue();
        Directory.Exists(ambientPackages).Should().BeFalse();
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
    [Timeout(10_000)]
    public void InstallFresh_ProcessTimeout_IsEndToEndBoundedAndCleansTemporaryOperation()
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

        Directory.GetDirectories(plan.StateRoot, ".cli-install-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
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
        string packagePath = Path.Join(directory, "package.nupkg");
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
