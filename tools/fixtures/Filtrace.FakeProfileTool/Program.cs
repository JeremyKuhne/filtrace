// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.FakeProfileTool;

/// <summary>
///  Provides a native argv boundary for the Track D profile contract.
/// </summary>
internal static class Program
{
    private const string ModeVariable = "FILTRACE_TRACKD_FAKE_PROFILE_MODE";
    private const string InvocationPathVariable = "FILTRACE_TRACKD_FAKE_PROFILE_INVOCATIONS";
    private const string MutationPathVariable = "FILTRACE_TRACKD_MUTATE_ANALYZER_DLL";

    /// <summary>
    ///  Emulates the bounded recorder and analyzer operations used by the contract test.
    /// </summary>
    /// <param name="args">Recorder or analyzer argument tokens.</param>
    /// <returns>The emulated native process exit code.</returns>
    public static int Main(string[] args)
    {
        RecordInvocation(args);
        string mode = Environment.GetEnvironmentVariable(ModeVariable) ?? "success";

        if (args is ["--version"])
        {
            Console.WriteLine("1.2.3+fake");
            return 0;
        }

        if (args is ["list-profiles"])
        {
            if (mode == "profiles-malformed")
            {
                Console.WriteLine("no parseable profiles");
                return 0;
            }

            Console.WriteLine("dotnet-common - Runtime diagnostics");
            Console.WriteLine("dotnet-sampled-thread-time (collect) - Managed CPU samples");
            Console.WriteLine("gc-verbose - Allocation samples");
            return mode == "profiles-nonzero" ? 7 : 0;
        }

        if (args.Length > 0 && args[0] == "collect")
        {
            return Collect(args, mode);
        }

        return Analyze(args, mode);
    }

    private static int Collect(string[] args, string mode)
    {
        string? output = GetOption(args, "--output");
        if (string.IsNullOrEmpty(output))
        {
            Console.Error.WriteLine("missing --output");
            return 2;
        }

        string? mutationPath = Environment.GetEnvironmentVariable(MutationPathVariable);
        if (!string.IsNullOrEmpty(mutationPath))
        {
            File.WriteAllText(mutationPath, "different managed implementation");
        }

        if (mode == "capture-nonzero")
        {
            Console.Error.WriteLine("injected capture failure");
            return 8;
        }

        if (mode == "capture-missing")
        {
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (mode == "capture-empty")
        {
            File.WriteAllBytes(output, []);
        }
        else
        {
            File.WriteAllText(output, $"fake trace: {GetOption(args, "--profile")}");
        }

        Console.WriteLine($"captured {output}");
        return 0;
    }

    private static int Analyze(string[] args, string mode)
    {
        if (mode == "analysis-nonzero")
        {
            Console.Error.WriteLine("injected analysis failure");
            return 9;
        }

        if (mode == "analysis-malformed")
        {
            Console.WriteLine("not json");
            return 0;
        }

        if (args.Length == 0)
        {
            return 2;
        }

        if (args[0] == "info")
        {
            int eventCount = mode == "analysis-valid-empty" ? 0 : 11;
            int gcEventCount = mode.StartsWith("gc-", StringComparison.Ordinal) ? 0 : eventCount;
            object analyses;
            if (mode == "analysis-missing")
            {
                analyses = new Dictionary<string, object>();
            }
            else
            {
                analyses = new
                {
                    cpu = new
                    {
                        captureStatus = "enabled",
                        eventCount
                    },
                    alloc = new
                    {
                        captureStatus = "enabled",
                        eventCount
                    },
                    gcstats = new
                    {
                        captureStatus = "enabled",
                        eventCount = gcEventCount
                    }
                };
            }

            string[] availableAnalyses = ["cpu", "alloc", "gcstats"];
            Dictionary<string, object> info = new();
            info["schemaVersion"] = 16;
            info["warnings"] = GetWarnings(mode);
            info["hints"] = Array.Empty<object>();
            info["context"] = new
            {
                operation = "info"
            };

            info["result"] = new
            {
                path = args.Length > 1 ? args[1] : "capture.nettrace",
                format = "NetTrace",
                totalWeight = eventCount,
                sampleCount = eventCount,
                symbolResolutionRate = 1.0,
                threads = new[]
                {
                    new
                    {
                        thread = "1",
                        sampleCount = eventCount
                    }
                },
                availableAnalyses,
                etlxCacheState = "converted",
                analyses = mode == "analysis-wrong-top-level" ? null : analyses,
                sourceResolution = new
                {
                    searchedDirectories = Array.Empty<string>(),
                    sampledManagedFrameCount = eventCount,
                    mappedManagedFrameCount = eventCount,
                    matchingPdbModules = Array.Empty<string>(),
                    highestUnmappedModules = Array.Empty<string>(),
                    highestUnmappedMethods = Array.Empty<string>()
                }
            };

            if (mode == "analysis-wrong-top-level")
            {
                info["analyses"] = analyses;
            }

            Console.WriteLine(JsonSerializer.Serialize(info));
            return 0;
        }

        if (args[0] is "rank" or "report")
        {
            if (args[0] == "rank")
            {
                WriteRank(args, mode);
            }
            else
            {
                WriteGcReport(mode);
            }

            return 0;
        }

        Console.Error.WriteLine($"unexpected operation '{args[0]}'");
        return 3;
    }

    private static void WriteRank(string[] args, string mode)
    {
        string metric = GetOption(args, "--metric") ?? "cpu";
        string measure = GetOption(args, "--measure") ?? "self";
        List<object> rows = [];
        if (mode != "analysis-empty-rank")
        {
            if (mode == "analysis-bad-rank-shape")
            {
                rows.Add(new { weight = "not-a-number" });
            }
            else
            {
                rows.Add(new
                {
                    frame = "Fake.Work",
                    weight = 11.0,
                    percentOfScope = 100.0
                });
            }
        }

        object[] warnings = GetWarnings(mode);

        object result;
        if (metric == "alloc" && mode != "analysis-invalid-record-count")
        {
            result = new
            {
                scopeWeight = rows.Count == 0 ? 0.0 : 11.0,
                rootFrame = "",
                rows
            };
        }
        else
        {
            result = new
            {
                scopeWeight = rows.Count == 0 ? 0.0 : 11.0,
                rootFrame = "",
                rows,
                contributingRecordCount = metric == "alloc" && mode == "analysis-invalid-record-count"
                    ? -1
                    : rows.Count == 0 ? 0 : 11
            };
        }

        object envelope = new
        {
            schemaVersion = 16,
            warnings,
            context = new
            {
                operation = "rank",
                metric,
                measure,
                unit = metric == "cpu" ? "ms" : "bytes"
            },
            result
        };

        Console.WriteLine(JsonSerializer.Serialize(envelope));
    }

    private static object[] GetWarnings(string mode) => mode switch
    {
        "analysis-low-quality" =>
        [
            new
            {
                code = "low_frame_resolution",
                severity = "warning",
                message = "Only 50% of frames resolved to a method name (< 80%); native frames may be unresolved."
            },
        ],
        "analysis-benign-warning" =>
        [
            new
            {
                code = "scope_applied",
                severity = "warning",
                message = "Process scope selected Fake.Process."
            },
        ],
        _ => []
    };

    private static void WriteGcReport(string mode)
    {
        if (mode == "gc-malformed")
        {
            Console.WriteLine("{\"schemaVersion\":15,\"warnings\":[],\"context\":{\"operation\":\"gc\"},\"result\":{}}");
            return;
        }

        if (mode == "gc-absent")
        {
            Console.WriteLine("{\"schemaVersion\":16,\"warnings\":[],\"context\":{\"operation\":\"gc\"}}");
            return;
        }

        bool empty = mode == "gc-valid-empty";
        List<object> records = [];
        if (!empty)
        {
            records.Add(new
            {
                number = 1,
                generation = 0,
                    kind = "Blocking",
                reason = "AllocSmall",
                pauseMs = 1.0,
                heapSizeAfterMB = 1.0,
                    promotedMB = 0.25
            });
        }

        object envelope = new
        {
            schemaVersion = 16,
            warnings = Array.Empty<object>(),
            context = new
            {
                operation = "gc"
            },
            result = new
            {
                gcCount = records.Count,
                gen0Count = records.Count,
                gen1Count = 0,
                gen2Count = 0,
                inducedCount = 0,
                totalPauseMs = empty ? 0.0 : 1.0,
                maxPauseMs = empty ? 0.0 : 1.0,
                meanPauseMs = empty ? 0.0 : 1.0,
                percentTimeInGc = empty ? 0.0 : 0.5,
                peakHeapSizeMB = empty ? 0.0 : 2.0,
                totalPromotedMB = empty ? 0.0 : 0.25,
                gcs = records
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(envelope));
    }

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void RecordInvocation(string[] args)
    {
        string? path = Environment.GetEnvironmentVariable(InvocationPathVariable);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Dictionary<string, object?> invocation = new();
        invocation["executable"] = Environment.ProcessPath;
        invocation["workingDirectory"] = Environment.CurrentDirectory;
        invocation["arguments"] = args;
        string record = JsonSerializer.Serialize(invocation);
        File.AppendAllText(path, $"{record}{Environment.NewLine}");
    }
}