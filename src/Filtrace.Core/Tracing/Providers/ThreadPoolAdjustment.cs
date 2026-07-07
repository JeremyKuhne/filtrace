// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  The count of worker-thread adjustments the runtime made for one reason, in a
///  <see cref="ThreadPoolResult"/>.
/// </summary>
/// <param name="Reason">The adjustment reason (for example <c>Starvation</c> or <c>ClimbingMove</c>).</param>
/// <param name="Count">How many adjustments the runtime made for that reason.</param>
public sealed record ThreadPoolAdjustment(string Reason, int Count);
