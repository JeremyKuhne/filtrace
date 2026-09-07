// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

#if FILTRACE_FASTTRACE
global using FastTrace;
global using FastTrace.Symbols;
global using FastTrace.Analysis;
global using FastTrace.Analysis.GC;
global using FastTrace.Analysis.JIT;
global using FastTrace.Computers;
global using FastTrace.Etlx;
global using FastTrace.EventPipe;
global using FastTrace.Parsers;
global using FastTrace.Parsers.Clr;
global using FastTrace.Parsers.Kernel;
global using FastTrace.Session;
global using FastTrace.Stacks;
global using FastSerialization = FastTrace.Serialization;

#if FILTRACE_HOT_LOOP_BENCH
global using Microsoft.Diagnostics.NETCore.Client;
global using FastTrace.Parsers.Symbol;
#endif

global using EtlxTraceLog = FastTrace.Etlx.TraceLog;
global using EtlxProcessIndex = FastTrace.Etlx.ProcessIndex;
global using EtlxTraceProcess = FastTrace.Etlx.TraceProcess;
global using AnalysisTraceProcess = FastTrace.Analysis.TraceProcess;
global using EtlxTraceThread = FastTrace.Etlx.TraceThread;
#else
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
#endif
