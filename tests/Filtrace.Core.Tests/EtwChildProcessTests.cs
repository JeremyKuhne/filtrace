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

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The process fixture uses cmd.exe.");
        }
    }
}