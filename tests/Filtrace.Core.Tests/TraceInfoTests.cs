// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class TraceInfoTests
{
    [TestMethod]
    public void Constructor_NullReferenceArgument_ThrowsArgumentNull()
    {
        IReadOnlyList<ThreadSampleInfo> threads = [];
        IReadOnlyList<string> warnings = [];
        IReadOnlyList<string> availableAnalyses = ["cpu"];
        IReadOnlyDictionary<string, AnalysisAvailability> analyses =
            new Dictionary<string, AnalysisAvailability>();

        Action nullPath = () => Create(null!, threads, warnings, availableAnalyses, analyses);
        Action nullThreads = () => Create("/trace.nettrace", null!, warnings, availableAnalyses, analyses);
        Action nullWarnings = () => Create("/trace.nettrace", threads, null!, availableAnalyses, analyses);
        Action nullAvailableAnalyses = () => Create("/trace.nettrace", threads, warnings, null!, analyses);
        Action nullAnalyses = () => Create("/trace.nettrace", threads, warnings, availableAnalyses, null!);

        nullPath.Should().Throw<ArgumentNullException>().WithParameterName("path");
        nullThreads.Should().Throw<ArgumentNullException>().WithParameterName("threads");
        nullWarnings.Should().Throw<ArgumentNullException>().WithParameterName("warnings");
        nullAvailableAnalyses.Should().Throw<ArgumentNullException>().WithParameterName("availableAnalyses");
        nullAnalyses.Should().Throw<ArgumentNullException>().WithParameterName("analyses");
    }

    private static TraceInfo Create(
        string path,
        IReadOnlyList<ThreadSampleInfo> threads,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> availableAnalyses,
        IReadOnlyDictionary<string, AnalysisAvailability> analyses) =>
        new(
            path,
            TraceFormat.NetTrace,
            1.0,
            1,
            1.0,
            threads,
            warnings,
            availableAnalyses,
            analyses);
}
