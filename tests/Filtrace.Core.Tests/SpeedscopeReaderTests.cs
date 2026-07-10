// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;

namespace Filtrace.Tracing.Readers;

[TestClass]
public sealed class SpeedscopeReaderTests
{
    [TestMethod]
    public void Read_EventedNanoseconds_NormalizesToMilliseconds()
    {
        const string json = """
            {"shared":{"frames":[{"name":"Work"}]},"profiles":[{"type":"evented","name":"thread","unit":"nanoseconds","startValue":0,"endValue":2000000,"events":[{"type":"O","frame":0,"at":0},{"type":"C","frame":0,"at":2000000}]}]}
            """;

        LoadedTrace trace = Read(json);

        trace.Info.TotalWeight.Should().Be(2.0);
        trace.Aggregator.SelfTime("", FrameNames.DefaultFoldPatterns, 5).Rows[0].Weight.Should().Be(2.0);
    }

    [TestMethod]
    public void Read_SampledSeconds_NormalizesToMilliseconds()
    {
        const string json = """
            {"shared":{"frames":[{"name":"Root"},{"name":"First"},{"name":"Second"}]},"profiles":[{"type":"sampled","name":"thread","unit":"seconds","startValue":0,"endValue":2,"samples":[[0,1],[0,2]],"weights":[0.5,1.5]}]}
            """;

        LoadedTrace trace = Read(json);
        RankingResult ranking = trace.Aggregator.SelfTime("", FrameNames.DefaultFoldPatterns, 5);

        trace.Info.TotalWeight.Should().Be(2000.0);
        ranking.Rows[0].Frame.Should().Be("Second");
        ranking.Rows[0].Weight.Should().Be(1500.0);
    }

    [TestMethod]
    public void Read_FiltraceCpuExport_RoundTripsSamples()
    {
        StackSampleSource source = new(
            MetricInfo.Cpu,
            [
                new SampleStack(["Root", "First"], 2.0),
                new SampleStack(["Root", "Second"], 3.0)
            ]);

        LoadedTrace trace = Read(SpeedscopeExporter.Export(source));
        RankingResult ranking = trace.Aggregator.SelfTime("", FrameNames.DefaultFoldPatterns, 5);

        trace.Info.TotalWeight.Should().Be(5.0);
        ranking.Rows.Select(static row => row.Frame).Should().Contain(["First", "Second"]);
    }

    [TestMethod]
    public void Read_ByteProfile_RejectsNonCpuUnit()
    {
        const string json = """
            {"shared":{"frames":[{"name":"Allocate"}]},"profiles":[{"type":"sampled","name":"alloc","unit":"bytes","startValue":0,"endValue":10,"samples":[[0]],"weights":[10]}]}
            """;

        Action action = () => Read(json);

        action.Should().Throw<NotSupportedException>().WithMessage("*requires a time unit*");
    }

    [TestMethod]
    public void Read_UnknownProfiles_DoNotInvalidateSupportedCpuProfile()
    {
        const string json = """
            {"shared":{"frames":[{"name":"Work"}]},"profiles":[{"type":"extension","name":"bytes","unit":"bytes"},{"type":"extension","name":"missing-unit"},{"type":"sampled","name":"cpu","unit":"milliseconds","startValue":0,"endValue":2,"samples":[[0]],"weights":[2]}]}
            """;

        LoadedTrace trace = Read(json);

        trace.Info.TotalWeight.Should().Be(2.0);
        trace.Aggregator.SelfTime("", FrameNames.DefaultFoldPatterns, 5).Rows[0].Frame.Should().Be("Work");
    }

    [TestMethod]
    public void Read_ProfileMissingType_RejectsMalformedInput()
    {
        const string json = """
            {"shared":{"frames":[]},"profiles":[{"name":"missing-type"}]}
            """;

        Action action = () => Read(json);

        action.Should().Throw<KeyNotFoundException>().WithMessage("*missing*required 'type' property*");
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("\"\"")]
    public void Read_ProfileWithNullOrEmptyType_RejectsMalformedInput(string typeValue)
    {
        string json = $$"""
            {"shared":{"frames":[]},"profiles":[{"type":{{typeValue}},"name":"invalid-type"}]}
            """;

        Action action = () => Read(json);

        action.Should().Throw<FormatException>().WithMessage("*type*non-empty string*");
    }

    private static LoadedTrace Read(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"filtrace-{Guid.NewGuid():N}.speedscope.json");
        File.WriteAllText(path, json);
        try
        {
            return new TraceLoader().Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}