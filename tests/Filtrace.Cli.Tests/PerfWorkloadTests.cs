// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.PerfWorkload;

[TestClass]
[DoNotParallelize]
public sealed class PerfWorkloadTests
{
    private static (int Exit, string Out, string Error) Run(params string[] args)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter output = new();
        using StringWriter error = new();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exit = Program.Main(args);
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    public void Main_Help_WritesUsageToStandardOutput(string argument)
    {
        (int exit, string output, string error) = Run(argument);

        exit.Should().Be(0);
        output.Should().StartWith("Usage:");
        error.Should().BeEmpty();
    }

    [TestMethod]
    public void Main_NoArguments_WritesUsageToStandardError()
    {
        (int exit, string output, string error) = Run();

        exit.Should().Be(2);
        output.Should().BeEmpty();
        error.Should().StartWith("Usage:");
    }
}