// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class AnalysisEventCounterTests
{
    [TestMethod]
    public void IsApplicationProvider_TplEventSource_ReturnsTrue()
    {
        AnalysisEventCounter.IsApplicationProvider("System.Threading.Tasks.TplEventSource")
            .Should().BeTrue();
    }

    [TestMethod]
    [DataRow("Microsoft-Windows-DotNETRuntime")]
    [DataRow("Microsoft-Windows-DotNETRuntimeRundown")]
    [DataRow("Microsoft-DotNETCore-SampleProfiler")]
    public void IsApplicationProvider_RuntimeProvider_ReturnsFalse(string providerName)
    {
        AnalysisEventCounter.IsApplicationProvider(providerName).Should().BeFalse();
    }
}