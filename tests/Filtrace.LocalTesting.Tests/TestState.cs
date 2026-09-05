// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

internal static class TestState
{
    public const string Hash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static LocalTestingState Create(LocalTestingStatus status)
    {
        return new()
        {
            SchemaVersion = LocalTestingState.CurrentSchemaVersion,
            Status = status,
            SourceCheckout = Path.GetFullPath(Path.Join(Path.GetTempPath(), "filtrace-source")),
            Baseline = new()
            {
                Mcp = new(),
                Skill = new(),
                CreatedDirectories = new()
            },
            Cli = status is LocalTestingStatus.Active
                ? new()
                {
                    PackageVersion = "1.2.3",
                    PackageSha256 = Hash
                }

                : null
        };
    }
}
