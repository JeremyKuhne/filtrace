// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Runs a disk-I/O request against the analysis core: read the physical disk
///  read/write records, cap the per-file detail to the heaviest files, wrap the
///  result in the output contract, and render it as text or JSON.
/// </summary>
/// <remarks>
///  <para>
///   Unlike the ranking verbs this is a structured report, not a stack ranking, so
///   it does not flow through the folding aggregator. The aggregate summary always
///   reflects every operation; only the per-file detail list is capped, ranked by
///   disk service time so the heaviest files are kept. A trace with no disk events
///   yields an empty report with an explanatory warning.
///  </para>
///  <para>
///   The execution is independent of the command-line parser; it takes its inputs
///   as a <see cref="DiskIoRequest"/> and writes to the supplied writers, so it can
///   be driven directly in tests as well as from the verb handler in
///   <see cref="TraceCommands"/>.
///  </para>
/// </remarks>
internal static class DiskIoExecutor
{
    /// <summary>
    ///  Executes the disk-I/O request.
    /// </summary>
    /// <param name="request">The validated disk-I/O inputs.</param>
    /// <param name="output">The writer the result is rendered to.</param>
    /// <param name="error">The writer load errors are reported to.</param>
    /// <returns>A process exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Run(DiskIoRequest request, TextWriter output, TextWriter error)
    {
        // Defensive: the verb enforces top >= 1, but Run is also called directly, so
        // guard the boundary rather than emit a confusing "top 0" report.
        if (request.Top < 1)
        {
            error.WriteLine("top must be 1 or greater.");
            return ExitCodes.UsageError;
        }

        if (!TraceExecution.TryReadEtlReport(
            request.Path,
            "disk I/O",
            () => new DiskIoProvider().Read(request.Path),
            error,
            out DiskIoResult? full))
        {
            return ExitCodes.InputError;
        }

        // Keep the full aggregate summary, but cap the per-file detail to the heaviest
        // files so a broad capture cannot blow the output budget. The empty case is shown
        // by the renderer (and the empty file list in JSON), like the other reports.
        List<string> warnings = [];
        IReadOnlyList<DiskIoFileRecord> shown = full.Files;
        if (shown.Count > request.Top)
        {
            shown = [.. shown.Take(request.Top)];
            warnings.Add($"Showing the top {request.Top} of {full.Files.Count} files by disk time.");
        }

        DiskIoResult report = full with { Files = shown };
        AnalysisResult<DiskIoResult> envelope = new(report, warnings);

        if (request.Format == OutputFormat.Json)
        {
            output.WriteLine(OutputJson.Serialize(envelope));
        }
        else
        {
            DiskIoTextRenderer.Render(envelope, request.Path, output);
        }

        return ExitCodes.Success;
    }
}
