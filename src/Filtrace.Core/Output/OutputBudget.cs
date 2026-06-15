// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Filtrace.Output;

/// <summary>
///  The output token budget: estimates how many tokens a serialized result will
///  cost an agent and decides when it exceeds the ceiling, so a verb can narrow
///  the query rather than flood the context window.
/// </summary>
/// <remarks>
///  <para>
///   Token counts are model-specific, so the estimate is deliberately offline and
///   dependency-free: it reproduces the GPT (cl100k_base) pre-tokenizer - the regex
///   that splits text into words, numbers, punctuation runs, and whitespace - then
///   models each piece as one or more sub-word tokens. Byte-pair encoding never
///   merges across those piece boundaries, so the piece count is a lower bound on
///   the real token count; this tracks punctuation-dense JSON (what the result
///   envelopes are) far better than a flat characters-per-token divisor, which can
///   under-count such text by a third. The estimate runs slightly conservative
///   (it over-counts a little), which is the safe direction for a ceiling.
///  </para>
///  <para>
///   The algorithm is mirrored byte-for-byte by tools/Get-TokenEstimate.ps1, which
///   the MCP server's schema-budget check uses; keep the pattern and the per-piece
///   math in sync across the two. It is a guardrail, not exact accounting: the
///   ceiling sits well below a typical context window, and no model's tokenizer is
///   reproduced exactly (Claude ships no public vocab), so small error is harmless.
///  </para>
///  <para>
///   This type only measures and warns. Actually truncating an over-budget
///   result - dropping rows or tightening the scope - is a per-verb concern that
///   lands with the CLI head; <see cref="TryGetBudgetWarning"/> is the building
///   block it consumes, mirroring how <c>SymbolGate</c> exposes its predicate.
///  </para>
/// </remarks>
public static partial class OutputBudget
{
    /// <summary>
    ///  The default ceiling, in estimated tokens, above which a result is considered
    ///  too large for an agent's context budget.
    /// </summary>
    public const int DefaultCeilingTokens = 25_000;

    /// <summary>
    ///  The per-piece sub-word divisor: a pre-tokenizer piece of <c>L</c> characters
    ///  is modeled as <c>ceil(L / CharsPerSubToken)</c> tokens (at least one).
    ///  Calibrated to 6 against tiktoken cl100k_base over filtrace's JSON, markdown,
    ///  and prose. Must match the value in tools/Get-TokenEstimate.ps1.
    /// </summary>
    private const double CharsPerSubToken = 6.0;

    /// <summary>
    ///  The GPT (cl100k_base) pre-tokenizer, adapted for .NET (greedy quantifiers in
    ///  place of the original's possessive ones, which produce the same piece
    ///  boundaries for counting). Ordered alternation: contractions, an optional
    ///  leading non-letter plus a word, a run of 1-3 digits, an optional leading
    ///  space plus a punctuation run, then whitespace / newline runs. Must match the
    ///  pattern and options in tools/Get-TokenEstimate.ps1.
    /// </summary>
    [GeneratedRegex(
        @"'(?:[sdmt]|ll|ve|re)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]|\s+(?!\S)|\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PreTokenizerRegex();

    /// <summary>
    ///  Estimates the token cost of <paramref name="text"/> with the offline
    ///  pre-tokenizer model (see the type remarks).
    /// </summary>
    /// <param name="text">The serialized output to estimate.</param>
    /// <returns>The estimated token count.</returns>
    public static int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return 0;
        }

        int tokens = 0;
        foreach (ValueMatch piece in PreTokenizerRegex().EnumerateMatches(text))
        {
            int length = piece.Length;
            if (length <= 0)
            {
                continue;
            }

            // Each piece is at least one token; long pieces split into sub-word
            // tokens. BPE never merges across piece boundaries, so this is a lower
            // bound nudged up by the sub-word term.
            tokens += Math.Max(1, (int)Math.Ceiling(length / CharsPerSubToken));
        }

        return tokens;
    }

    /// <summary>
    ///  Determines whether <paramref name="text"/> exceeds the token ceiling.
    /// </summary>
    /// <param name="text">The serialized output to measure.</param>
    /// <param name="ceilingTokens">The token ceiling.</param>
    /// <returns><see langword="true"/> when the estimate exceeds the ceiling.</returns>
    public static bool IsOverBudget(string text, int ceilingTokens = DefaultCeilingTokens) =>
        EstimateTokens(text) > ceilingTokens;

    /// <summary>
    ///  Produces a budget warning, with remediation, when <paramref name="text"/>
    ///  exceeds the token ceiling.
    /// </summary>
    /// <param name="text">The serialized output to measure.</param>
    /// <param name="ceilingTokens">The token ceiling.</param>
    /// <param name="warning">The warning text when over budget, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a warning was produced.</returns>
    public static bool TryGetBudgetWarning(string text, int ceilingTokens, [NotNullWhen(true)] out string? warning)
    {
        int tokens = EstimateTokens(text);
        if (tokens <= ceilingTokens)
        {
            warning = null;
            return false;
        }

        warning =
            $"Output is about {tokens} tokens, over the {ceilingTokens}-token budget. "
            + "Narrow the query (a smaller --top, a --root scope, or a tighter filter) to reduce it.";
        return true;
    }
}
