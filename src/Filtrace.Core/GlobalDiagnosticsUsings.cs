// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

global using Microsoft.Diagnostics.Symbols;
global using Microsoft.Diagnostics.Tracing;
global using Microsoft.Diagnostics.Tracing.Analysis;
global using Microsoft.Diagnostics.Tracing.Analysis.GC;
global using Microsoft.Diagnostics.Tracing.Analysis.JIT;
global using Microsoft.Diagnostics.Tracing.Computers;
global using Microsoft.Diagnostics.Tracing.Etlx;
global using Microsoft.Diagnostics.Tracing.EventPipe;
global using Microsoft.Diagnostics.Tracing.Parsers;
global using Microsoft.Diagnostics.Tracing.Parsers.Clr;
global using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
global using Microsoft.Diagnostics.Tracing.Session;
global using Microsoft.Diagnostics.Tracing.Stacks;

#if FILTRACE_HOT_LOOP_BENCH
global using Microsoft.Diagnostics.NETCore.Client;
global using Microsoft.Diagnostics.Tracing.Parsers.Symbol;
#endif

global using EtlxTraceLog = Microsoft.Diagnostics.Tracing.Etlx.TraceLog;
global using EtlxProcessIndex = Microsoft.Diagnostics.Tracing.Etlx.ProcessIndex;
global using EtlxTraceProcess = Microsoft.Diagnostics.Tracing.Etlx.TraceProcess;
global using AnalysisTraceProcess = Microsoft.Diagnostics.Tracing.Analysis.TraceProcess;
global using EtlxTraceThread = Microsoft.Diagnostics.Tracing.Etlx.TraceThread;
