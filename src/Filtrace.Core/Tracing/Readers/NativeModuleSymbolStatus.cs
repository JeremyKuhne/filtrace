// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

/// <summary>
///  The result of a local native symbol lookup for one module, with the share of
///  unresolved sampled frames that module accounted for.
/// </summary>
/// <param name="ModuleName">The module's name, without extension.</param>
/// <param name="Status">What the lookup found.</param>
/// <param name="UnresolvedFrames">Sampled frames in the module that carried no method.</param>
/// <param name="UnresolvedShare">
///  <paramref name="UnresolvedFrames"/> as a share of all unresolved sampled frames.
/// </param>
internal sealed record NativeModuleSymbolStatus(
    string ModuleName,
    NativeSymbolStatus Status,
    int UnresolvedFrames,
    double UnresolvedShare);
