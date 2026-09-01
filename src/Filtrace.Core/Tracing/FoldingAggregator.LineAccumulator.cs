// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

public sealed partial class FoldingAggregator
{
    /// <summary>
    ///  Accumulates the self-weight and sample count attributed to one source line,
    ///  tracking which method dominates the line's weight.
    /// </summary>
    private sealed class LineAccumulator
    {
        private readonly Dictionary<string, double> _methods = new(StringComparer.Ordinal);

        /// <summary>
        ///  Gets the total sample weight attributed to this source line.
        /// </summary>
        public double Weight { get; private set; }

        /// <summary>
        ///  Gets the number of source records attributed to this line.
        /// </summary>
        public int SampleCount { get; private set; }

        /// <summary>
        ///  Adds one weighted record and credits its method for dominant-method selection.
        /// </summary>
        /// <param name="weight">The record weight to add.</param>
        /// <param name="method">The normalized method name associated with the record.</param>
        public void Add(double weight, string method)
        {
            Weight += weight;
            SampleCount++;
            _methods.TryGetValue(method, out double current);
            _methods[method] = current + weight;
        }

        /// <summary>
        ///  Gets the method contributing the greatest weight, with ordinal insertion order breaking ties.
        /// </summary>
        public string DominantMethod
        {
            get
            {
                string dominant = "";
                double dominantMs = -1.0;
                foreach (KeyValuePair<string, double> pair in _methods)
                {
                    if (pair.Value > dominantMs)
                    {
                        dominantMs = pair.Value;
                        dominant = pair.Key;
                    }
                }

                return dominant;
            }
        }
    }
}
