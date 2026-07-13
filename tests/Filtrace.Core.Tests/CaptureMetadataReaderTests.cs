// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

[TestClass]
public sealed class CaptureMetadataReaderTests
{
    [TestMethod]
    public void Read_ValidMetadata_ReturnsKnownStatuses()
    {
        string trace = CreateTrace(out string directory);
        try
        {
            File.WriteAllText(
                CaptureMetadataReader.PathFor(trace),
                """{"schemaVersion":1,"analyses":{"cpu":"enabled","alloc":"disabled","future":"enabled"}}""");
            List<string> warnings = [];

            IReadOnlyDictionary<string, CaptureStatus>? result =
                CaptureMetadataReader.Read(trace, warnings);

            result.Should().BeEquivalentTo(new Dictionary<string, CaptureStatus>
            {
                ["cpu"] = CaptureStatus.Enabled,
                ["alloc"] = CaptureStatus.Disabled
            });
            warnings.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Read_MalformedMetadata_WarnsAndReturnsNull()
    {
        string trace = CreateTrace(out string directory);
        try
        {
            File.WriteAllText(CaptureMetadataReader.PathFor(trace), "{not-json");
            List<string> warnings = [];

            IReadOnlyDictionary<string, CaptureStatus>? result =
                CaptureMetadataReader.Read(trace, warnings);

            result.Should().BeNull();
            warnings.Should().ContainSingle().Which.Should().Contain("Provider enablement remains unknown");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Read_OversizedMetadata_WarnsAndReturnsNull()
    {
        string trace = CreateTrace(out string directory);
        try
        {
            File.WriteAllBytes(
                CaptureMetadataReader.PathFor(trace),
                new byte[CaptureMetadataReader.MaxBytes + 1]);
            List<string> warnings = [];

            IReadOnlyDictionary<string, CaptureStatus>? result =
                CaptureMetadataReader.Read(trace, warnings);

            result.Should().BeNull();
            warnings.Should().ContainSingle().Which.Should().Contain("exceeds");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Read_NoMetadata_ReturnsNullWithoutWarning()
    {
        string trace = CreateTrace(out string directory);
        try
        {
            List<string> warnings = [];

            CaptureMetadataReader.Read(trace, warnings).Should().BeNull();
            warnings.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTrace(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), $"filtrace-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string trace = Path.Combine(directory, "sample.nettrace");
        File.WriteAllBytes(trace, []);
        return trace;
    }
}