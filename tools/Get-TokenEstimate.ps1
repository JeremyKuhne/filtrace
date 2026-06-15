#!/usr/bin/env pwsh
# Copyright (c) 2025 Jeremy W Kuhne
# SPDX-License-Identifier: MIT
# See LICENSE file in the project root for full license information

<#
.SYNOPSIS
  Deterministic, offline token-count estimator for budget checks.

.DESCRIPTION
  Estimates how many LLM tokens a string will cost without calling any API or
  shipping a multi-megabyte BPE vocabulary. It reproduces the *pre-tokenizer*
  stage of the GPT (cl100k_base / o200k_base) tokenizer - the regex that splits
  text into words, numbers, punctuation runs, and whitespace - then applies a
  small calibrated sub-word model to each piece.

  Why a pre-tokenizer estimate beats the flat four-characters-per-token rule:
  byte-pair encoding never merges across the pre-tokenizer's piece boundaries, so
  the piece count is a hard lower bound on the real token count. Punctuation-heavy
  text (JSON, the MCP tool schema) is split into many short pieces that the flat
  char/4 rule badly under-counts; prose is split into few. Counting pieces (plus a
  sub-word term for long pieces) tracks both far better than a single divisor.

  This is an estimator, not a tokenizer: it targets within ~10-15% of cl100k_base,
  which is itself only an approximation for non-OpenAI models (Claude does not use
  tiktoken and ships no public vocab). For regression gating - "did this surface
  grow" / "is it under the ceiling" - a deterministic, stable, slightly
  conservative estimate is exactly right; vendor-exact counts are not needed.

  Measured against tiktoken cl100k_base over a filtrace corpus (the captured MCP
  tool-list schema, result-envelope goldens, a speedscope export, the skill, the
  docs, and C# sources):
    - On JSON - what the budgets actually measure - mean absolute error is ~6%,
      versus ~17% for the flat char/4 rule.
    - The estimate is consistently conservative (it over-counts by ~10-15%), which
      is the safe direction for a ceiling: it trips slightly early rather than
      letting an over-budget payload through.
    - char/4 under-counts dense JSON by up to ~33% (a result 33% over budget would
      read as under it); this estimator's worst under-count on the same corpus is
      ~1%. Removing that blind spot is the whole point.


.PARAMETER Path
  A file whose contents to estimate. Mutually exclusive with -Text.

.PARAMETER Text
  A string to estimate. Mutually exclusive with -Path. Also accepts pipeline input.

.EXAMPLE
  ./tools/Get-TokenEstimate.ps1 -Path README.md

.EXAMPLE
  $schema | ./tools/Get-TokenEstimate.ps1

.NOTES
  Dot-source the script to reuse the function directly:
    . ./tools/Get-TokenEstimate.ps1
    Get-TokenEstimate -Text $json
#>
[CmdletBinding(DefaultParameterSetName = 'Text')]
param(
    [Parameter(ParameterSetName = 'Path', Mandatory)]
    [string]$Path,

    [Parameter(ParameterSetName = 'Text', ValueFromPipeline)]
    [string]$Text
)

# The GPT (cl100k_base) pre-tokenizer pattern, adapted for .NET. The original uses
# possessive quantifiers (`?+`, `++`) which .NET does not support; greedy forms
# produce the same piece boundaries for counting. Ordered alternation, matched
# left to right:
#   1. contractions ('s 't 'll 've 're ...), case-insensitive
#   2. an optional single leading non-letter, then a run of letters (a word, with
#      its leading space - cl100k attaches one leading space to the following word)
#   3. a run of 1-3 digits (cl100k caps number tokens at three digits)
#   4. an optional leading space, then a run of punctuation/symbols
#   5. trailing newlines / whitespace runs
# IgnoreCase + CultureInvariant make the contraction match case- and culture-
# independent so this stays byte-for-byte identical to the C# OutputBudget mirror.
$script:PreTokenizerRegex = [regex]::new(
    "'(?:[sdmt]|ll|ve|re)" +
    "|[^\r\n\p{L}\p{N}]?\p{L}+" +
    "|\p{N}{1,3}" +
    "| ?[^\s\p{L}\p{N}]+[\r\n]*" +
    "|\s*[\r\n]|\s+(?!\S)|\s+",
    [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant, Compiled')

function Get-TokenEstimate {
    <#
    .SYNOPSIS
      Estimate the token count of a string (see the script header for the model).
    .PARAMETER Text
      The text to estimate.
    .PARAMETER CharsPerSubToken
      The per-piece sub-word divisor: a pre-tokenizer piece of L characters is
      modeled as ceil(L / CharsPerSubToken) tokens (at least 1). Calibrated to 6
      against cl100k_base over filtrace's JSON, markdown, and prose. Exposed for
      calibration sweeps; leave at the default for normal use.
    .OUTPUTS
      [int] the estimated token count.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [AllowEmptyString()]
        [string]$Text,

        [double]$CharsPerSubToken = 6.0
    )
    process {
        if ([string]::IsNullOrEmpty($Text)) { return 0 }

        $tokens = 0
        $matches = $script:PreTokenizerRegex.Matches($Text)
        foreach ($m in $matches) {
            $len = $m.Length
            if ($len -le 0) { continue }
            # Each piece is at least one token; long pieces split into sub-word
            # tokens. BPE never merges across piece boundaries, so this is a
            # lower bound nudged up by the sub-word term.
            $sub = [math]::Ceiling($len / $CharsPerSubToken)
            $tokens += [math]::Max(1, [int]$sub)
        }
        return $tokens
    }
}

# When run as a script (not dot-sourced), estimate the requested input and print
# the count. Dot-sourcing defines the function only and skips this block.
if ($MyInvocation.InvocationName -ne '.') {
    $input_text =
        if ($PSCmdlet.ParameterSetName -eq 'Path') {
            if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }
            Get-Content -LiteralPath $Path -Raw
        }
        else {
            $Text
        }

    if ($null -eq $input_text) { $input_text = '' }
    Get-TokenEstimate -Text $input_text
}
