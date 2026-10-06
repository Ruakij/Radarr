using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class IndexerResponseTimeHistoryFixture : CoreTest<IndexerResponseTimeHistory>
    {
        [Test]
        public void should_interpolate_percentiles()
        {
            var values = Enumerable.Range(0, 101).Select(v => (double)v).ToArray();

            IndexerResponseTimeHistory.Percentile(values, 2.5).Should().Be(2.5);
            IndexerResponseTimeHistory.Percentile(values, 97.5).Should().Be(97.5);
            IndexerResponseTimeHistory.Percentile(new[] { 7.0 }, 97.5).Should().Be(7);
        }

        [Test]
        public void should_take_median_of_unsorted_values()
        {
            IndexerResponseTimeHistory.Median(new[] { 3.0, 1, 2 }).Should().Be(2);
            IndexerResponseTimeHistory.Median(new[] { 10.0, 1, 3, 2 }).Should().Be(2.5);
        }

        [Test]
        public void should_keep_only_the_last_queries_per_indexer()
        {
            for (var i = 0; i < 150; i++)
            {
                Subject.Add(1, i);
            }

            Subject.Add(2, 1000);

            Subject.Get(1).Should().Be(new IndexerResponseTimes(100, 99.5, 52.475, 146.525));
            Subject.Get(2).Should().Be(new IndexerResponseTimes(1, 1000, 1000, 1000));
            Subject.Get(3).Should().BeNull();
        }
    }
}
