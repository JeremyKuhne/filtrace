// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Runs a process-inventory request against the analysis core: load every process
///  in the trace, rank them by weight, wrap the result in the output contract, and
///  render it as text or JSON.
/// </summary>
/// <remarks>
///  <para>
///   The load is pinned to <see cref="ScopeRequest.AllProcesses"/>: the inventory's
///   whole purpose is to show every process so the caller can choose one to scope a
///   ranking to, so it must not auto-scope to the busiest. The execution is
///   independent of the command-line parser; it takes its inputs as a
///   <see cref="ProcessesRequest"/> and writes to the supplied writers, so it can be
///   driven directly in tests as well as from the verb handler in
///   <see cref="TraceCommands"/>.
///  </para>
/// </remarks>
internal static class ProcessesExecutor
{
    /// <summary>
    ///  Executes the process-inventory request.
    /// </summary>
    /// <param name="request">The validated inventory inputs.</param>
    /// <param name="output">The writer the result is rendered to.</param>
    /// <param name="error">The writer load errors are reported to.</param>
    /// <returns>A process exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Run(ProcessesRequest request, TextWriter output, TextWriter error)
    {
        ProcessInventorySnapshot inventory;
        try
        {
            inventory = new ProcessInventoryProvider().Read(request.Path);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or System.Text.Json.JsonException
                or KeyNotFoundException
                or InvalidOperationException
                or FormatException
                or ArgumentException)
        {
            error.WriteLine(ex.Message);
            return ExitCodes.InputError;
        }

        AnalysisResult<ProcessListResult> envelope = new(
            inventory.Result,
            inventory.Warnings,
            context: new AnalysisContext("processes")
            {
                Metric = "cpu",
                Unit = inventory.Metric.Unit,
                CpuSampling = inventory.CpuSampling
            });

        if (request.Format == OutputFormat.Json)
        {
            output.WriteLine(OutputJson.Serialize(envelope));
        }
        else
        {
            ProcessesTextRenderer.Render(envelope, inventory, output);
        }

        return ExitCodes.Success;
    }
}
