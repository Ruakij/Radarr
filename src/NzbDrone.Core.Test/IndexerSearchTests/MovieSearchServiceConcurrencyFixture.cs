using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class MovieSearchServiceConcurrencyFixture : CoreTest<MovieSearchService>
    {
        private const int MovieCount = 5;

        private static DownloadDecision Decision(int movieId, string title)
        {
            return new DownloadDecision(new RemoteMovie { Movie = new Movie { Id = movieId }, Release = new ReleaseInfo { Title = title } });
        }

        // Movies are searched by last search time in id order, lower ids search slower,
        // movie 3 fails and every movie grabs its first release.
        private List<List<string>> GivenMovieSearches(int concurrency, Action onStart = null, Action onEnd = null)
        {
            var processed = new List<List<string>>();

            var movies = Enumerable.Range(1, MovieCount)
                                   .Select(id => new Movie { Id = id, Monitored = true, MinimumAvailability = MovieStatusType.Announced, LastSearchTime = DateTime.UtcNow.AddDays(id - MovieCount) })
                                   .Reverse()
                                   .ToList();

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(movies);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.SearchConcurrency)
                  .Returns(concurrency);

            Mocker.GetMock<ISearchForReleases>()
                  .Setup(s => s.MovieSearch(It.IsAny<int>(), It.IsAny<bool>(), false))
                  .Returns<int, bool, bool>(async (movieId, userInvokedSearch, interactiveSearch) =>
                  {
                      onStart?.Invoke();
                      await Task.Delay((MovieCount - movieId + 1) * 20);
                      onEnd?.Invoke();

                      if (movieId == 3)
                      {
                          throw new InvalidOperationException("Indexer failed");
                      }

                      return new List<DownloadDecision> { Decision(movieId, $"{movieId}-a"), Decision(movieId, $"{movieId}-b") };
                  });

            Mocker.GetMock<IProcessDownloadDecisions>()
                  .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                  .Returns<List<DownloadDecision>>(decisions =>
                  {
                      processed.Add(decisions.Select(d => d.RemoteMovie.Release.Title).ToList());

                      return Task.FromResult(new ProcessedDecisions(decisions.Take(1).ToList(), new List<DownloadDecision>(), new List<DownloadDecision>()));
                  });

            return processed;
        }

        private void Search()
        {
            Subject.Execute(new MoviesSearchCommand { MovieIds = Enumerable.Range(1, MovieCount).ToList(), Trigger = CommandTrigger.Manual });
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        public void should_run_searches_up_to_concurrency(int concurrency)
        {
            var running = 0;
            var maxRunning = 0;

            GivenMovieSearches(concurrency, () => maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running)), () => Interlocked.Decrement(ref running));

            Search();

            maxRunning.Should().Be(concurrency);
            ExceptionVerification.IgnoreErrors();
        }

        [TestCase(2)]
        [TestCase(10)]
        public void should_process_movies_like_sequential_search(int concurrency)
        {
            var sequential = GivenMovieSearches(1);
            Search();

            var parallel = GivenMovieSearches(concurrency);
            Search();

            var expected = new List<List<string>>
            {
                new List<string> { "1-a", "1-b" },
                new List<string> { "2-a", "2-b" },
                new List<string> { "4-a", "4-b" },
                new List<string> { "5-a", "5-b" }
            };

            sequential.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());

            parallel.Should().BeEquivalentTo(sequential, o => o.WithStrictOrdering());
            ExceptionVerification.IgnoreErrors();
        }

        [Test]
        public async Task should_sum_processed_counts_in_order()
        {
            var order = new List<int>();

            var count = await MovieSearchService.SearchAndProcess(new[] { 1, 2, 3 },
                3,
                n => Task.FromResult(n * 10),
                (n, result) =>
                {
                    order.Add(n);
                    return Task.FromResult(result);
                });

            count.Should().Be(60);
            order.Should().Equal(1, 2, 3);
        }
    }
}
