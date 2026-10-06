using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.IndexerSearch
{
    // Kept in memory only, so it starts empty after a restart
    public class IndexerResponseTimeHistory
    {
        public const int Size = 100;

        private readonly ConcurrentDictionary<int, Queue<double>> _durations = new ConcurrentDictionary<int, Queue<double>>();

        public void Add(int indexerId, double durationMs)
        {
            var queue = _durations.GetOrAdd(indexerId, _ => new Queue<double>());

            lock (queue)
            {
                queue.Enqueue(durationMs);

                if (queue.Count > Size)
                {
                    queue.Dequeue();
                }
            }
        }

        public IndexerResponseTimes Get(int indexerId)
        {
            if (!_durations.TryGetValue(indexerId, out var queue))
            {
                return null;
            }

            double[] sorted;

            lock (queue)
            {
                sorted = queue.OrderBy(d => d).ToArray();
            }

            return sorted.Length == 0
                ? null
                : new IndexerResponseTimes(sorted.Length, Percentile(sorted, 50), Percentile(sorted, 2.5), Percentile(sorted, 97.5));
        }

        public static double Median(IEnumerable<double> values)
        {
            return Percentile(values.OrderBy(d => d).ToArray(), 50);
        }

        // Interpolates linearly between the closest ranks of the sorted values
        public static double Percentile(double[] sorted, double percentile)
        {
            var rank = (sorted.Length - 1) * percentile / 100;
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);

            return sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
        }
    }
}
