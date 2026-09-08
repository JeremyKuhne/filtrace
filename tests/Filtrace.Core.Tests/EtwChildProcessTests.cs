// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics;
using Filtrace.Tracing;

namespace Filtrace.Core.Tests;

[TestClass]
public sealed class EtwChildProcessTests
{
    [TestMethod]
    public void Run_RedirectedNoisyFailingChild_DrainsAndIdentifiesBothStreams()
    {
        RequireWindows();
        string script = CreateScript(
                        """
                        @echo off
                        for /L %%i in (1,1,12000) do (
                            echo stdout-%%i
                            echo stderr-%%i 1>&2
                        )
                        echo {"result":"not-the-capture"}
                        exit /b 7
                        """);

        try
        {
            StringWriter subjectLog = new();
            EtwInvocation result = EtwChildProcess.Run(
                StartInfo(script), 1, 30, subjectLog, subjectLog);

            result.ExitCode.Should().Be(7);
            string log = subjectLog.ToString();
            log.Should().Contain("[subject stdout]");
            log.Should().Contain("[subject stderr]");
            log.Should().Contain("stdout-12000");
            log.Should().Contain("stderr-12000");
            log.Should().Contain("{\"result\":\"not-the-capture\"}");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [TestMethod]
    public void Run_RedirectedEmptyChild_WritesNoStreamMarkers()
    {
        RequireWindows();
        string script = CreateScript("@exit /b 0\r\n");

        try
        {
            StringWriter subjectLog = new();
            EtwInvocation result = EtwChildProcess.Run(
                StartInfo(script), 1, 30, subjectLog, subjectLog);

            result.ExitCode.Should().Be(0);
            subjectLog.ToString().Should().BeEmpty();
        }
        finally
        {
            File.Delete(script);
        }
    }

    [TestMethod]
    public void Run_RedirectedChildAtDurationCap_DrainsOutputAndReportsTermination()
    {
        RequireWindows();
        string script = CreateScript(
            """
            @echo before-timeout
            @echo error-before-timeout 1>&2
            @ping 127.0.0.1 -n 30 > nul
            """);

        try
        {
            StringWriter subjectLog = new();
            EtwInvocation result = EtwChildProcess.Run(
                StartInfo(script), 1, 1, subjectLog, subjectLog);

            result.ExitCode.Should().Be(-1);
            subjectLog.ToString().Should().Contain("before-timeout");
            subjectLog.ToString().Should().Contain("error-before-timeout");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [TestMethod]
    public void Run_RootExitsWhileDescendantInheritsPipes_ReturnsAfterDrainGrace()
    {
        RequireWindows();
        string descendantScript = CreatePowerShellScript("Start-Sleep -Seconds 30\r\n");
        string descendantPidPath = Path.Join(Path.GetTempPath(), $"filtrace-descendant-{Guid.NewGuid():N}.pid");
        string rootScript = CreatePowerShellScript(
            """
            param(
                [string] $DescendantScript,
                [string] $DescendantPidPath
            )

            $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = "$PSHOME\powershell.exe"
            $startInfo.UseShellExecute = $false
            $startInfo.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$DescendantScript`""
            $descendant = [System.Diagnostics.Process]::Start($startInfo)
            $descendant.Id | Set-Content -LiteralPath $DescendantPidPath -Encoding ascii
            $descendant.Dispose()

            [Console]::Out.WriteLine('root-stdout-before-exit')
            [Console]::Error.WriteLine('root-stderr-before-exit')
            exit 23
            """);

        int? descendantPid = null;
        try
        {
            StringWriter subjectLog = new();
            Stopwatch stopwatch = Stopwatch.StartNew();
            ProcessStartInfo startInfo = PowerShellStartInfo(
                rootScript, descendantScript, descendantPidPath);

            EtwInvocation result = EtwChildProcess.Run(
                startInfo, 1, 30, subjectLog, subjectLog);

            stopwatch.Stop();

            descendantPid = int.Parse(File.ReadAllText(descendantPidPath));
            result.ExitCode.Should().Be(23);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
            (DateTimeOffset.UtcNow - result.StoppedUtc).Should().BeGreaterThan(TimeSpan.FromSeconds(1));
            string log = subjectLog.ToString();
            log.Should().Contain("[subject stdout]");
            log.Should().Contain("[subject stderr]");
            log.Should().Contain("root-stdout-before-exit");
            log.Should().Contain("root-stderr-before-exit");
        }
        finally
        {
            if (descendantPid is null && File.Exists(descendantPidPath))
            {
                descendantPid = int.Parse(File.ReadAllText(descendantPidPath));
            }

            if (descendantPid is int processId)
            {
                try
                {
                    using Process descendant = Process.GetProcessById(processId);
                    if (!descendant.HasExited)
                    {
                        descendant.Kill(entireProcessTree: true);
                        descendant.WaitForExit(5000).Should().BeTrue();
                    }
                }
                catch (ArgumentException)
                {
                    // The bounded fixture may have exited before cleanup.
                }
            }

            File.Delete(rootScript);
            File.Delete(descendantScript);
            File.Delete(descendantPidPath);
        }
    }

    private static ProcessStartInfo StartInfo(string script) => new("cmd.exe")
    {
        Arguments = $"/d /c \"{script}\"",
        UseShellExecute = false,
    };

    private static string CreateScript(string content)
    {
        string path = Path.Join(Path.GetTempPath(), $"filtrace-child-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, content);
        return path;
    }

    private static ProcessStartInfo PowerShellStartInfo(string script, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string CreatePowerShellScript(string content)
    {
        string path = Path.Join(Path.GetTempPath(), $"filtrace-child-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, content);
        return path;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The process fixture uses cmd.exe.");
        }
    }
}