// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class ProcessOutputCaptureTests
{
    [TestMethod]
    public void Constructor_NegativeMaximum_Throws()
    {
        Action action = () => _ = new ProcessOutputCapture(-1);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maximumCharacters");
    }

    [TestMethod]
    public void Append_ZeroMaximum_DiscardsAllAndReportsTruncation()
    {
        ProcessOutputCapture capture = new(0);

        capture.Append("a".ToCharArray(), 1);

        capture.Snapshot().Should().Be((string.Empty, true));
    }

    [TestMethod]
    public void Append_ExactCapacity_RetainsAllWithoutTruncation()
    {
        ProcessOutputCapture capture = new(3);

        capture.Append("abc".ToCharArray(), 3);

        capture.Snapshot().Should().Be(("abc", false));
    }

    [TestMethod]
    public void Append_OverCapacity_RetainsLimitAndReportsTruncation()
    {
        ProcessOutputCapture capture = new(3);

        capture.Append("abcd".ToCharArray(), 4);

        capture.Snapshot().Should().Be(("abc", true));
    }
}