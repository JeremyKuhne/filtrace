// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>Renders one compact case row per manifest ranking.</summary>
internal static class BatchTextRenderer
{
    private const int IdentityWidth = 42;
    private const int WeightWidth = 14;
    private const int PercentWidth = 8;
    private const int RecordsWidth = 9;

    public static void Render(AnalysisResult<BatchRankingResult> envelope, TextWriter output)
    {
        BatchRankingResult result = envelope.Result;
        output.WriteLine(
            $"{result.Metric} {result.Measure} manifest batch  -  {result.Cases.Count} case(s)"
            + (string.IsNullOrEmpty(result.RootFrame) ? string.Empty : $"  root={result.RootFrame}"));
        output.WriteLine(
            $"  {"benchmark / parameters",-IdentityWidth}  {"scope",WeightWidth}  {"top",WeightWidth}  "
            + $"{"top %",PercentWidth}  {"records",RecordsWidth}  frame");
        foreach (BatchRankingCaseResult captureCase in result.Cases)
        {
            string identity = string.IsNullOrEmpty(captureCase.Parameters)
                ? captureCase.Benchmark
                : $"{captureCase.Benchmark} ({captureCase.Parameters})";
            string records = captureCase.ContributingRecordCount?.ToString() ?? "n/a";
            output.WriteLine(
                $"  {Trim(identity, IdentityWidth),-IdentityWidth}  "
                + $"{$"{captureCase.ScopeWeight:N2} {captureCase.Unit}",WeightWidth}  "
                + $"{$"{captureCase.TopWeight:N2} {captureCase.Unit}",WeightWidth}  "
                + $"{captureCase.TopPercentOfScope,PercentWidth:N2}  {records,RecordsWidth}  "
                + $"{captureCase.TopFrame ?? "(none)"}");
            if (captureCase.OperationUnit is not null)
            {
                output.WriteLine(
                    $"  {"",-IdentityWidth}  per {captureCase.OperationUnit}: "
                    + $"scope {captureCase.ScopeWeightPerOperation:N4}, top {captureCase.TopWeightPerOperation:N4} {captureCase.Unit}");
            }

            foreach (string warning in captureCase.Warnings)
            {
                output.WriteLine($"  ! [{captureCase.Benchmark}] {warning}");
            }
        }

        foreach (string hint in envelope.Hints)
        {
            output.WriteLine($"> {hint}");
        }
    }

    private static string Trim(string value, int width) =>
        value.Length <= width ? value : $"{value[..(width - 3)]}...";
}