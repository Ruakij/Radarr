using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
        public class ReleaseSearchServiceFixture : CoreTest<ReleaseSearchService>
    {
        private Mock<IIndexer> _mockIndexer;
        private Movie _movie;
        private TaskCompletionSource<IList<ReleaseInfo>> _neverAnswers;

        [SetUp]
        public void SetUp()
        {
            _mockIndexer = Mocker.GetMock<IIndexer>();
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            _mockIndexer.SetupGet(s => s.SupportsSearch).Returns(true);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _mockIndexer.Object });

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<Parser.Model.ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns(new List<DownloadDecision>());

            _neverAnswers = new TaskCompletionSource<IList<ReleaseInfo>>();

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            Mocker.GetMock<IMovieTranslationService>()
                .Setup(s => s.GetAllTranslationsForMovieMetadata(It.IsAny<int>()))
                .Returns(new List<MovieTranslation>());
        }

        [TearDown]
        public void TearDown()
        {
            _neverAnswers.TrySetResult(new List<ReleaseInfo>());
        }

        private List<SearchCriteriaBase> WatchForSearchCriteria()
        {
            var result = new List<SearchCriteriaBase>();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<MovieSearchCriteria>()))
                .Callback<MovieSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            return result;
        }

        [Test]
        public async Task Tags_IndexerTags_MovieNoTags_IndexerNotIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 3 }
            });

            var allCriteria = WatchForSearchCriteria();

            await Subject.MovieSearch(_movie, true, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task Tags_IndexerNoTags_MovieTags_IndexerIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1
            });

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 3 })
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            var allCriteria = WatchForSearchCriteria();

            await Subject.MovieSearch(_movie, true, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
        }

        [Test]
        public async Task Tags_IndexerAndMovieTagsMatch_IndexerIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 1, 2, 3 }
            });

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 3, 4, 5 })
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            var allCriteria = WatchForSearchCriteria();

            await Subject.MovieSearch(_movie, true, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
        }

        [Test]
        public async Task Tags_IndexerAndMovieTagsMismatch_IndexerNotIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 1, 2, 3 }
            });

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 4, 5, 6 })
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            var allCriteria = WatchForSearchCriteria();

            await Subject.MovieSearch(_movie, true, false);

            var criteria = allCriteria.OfType<MovieSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        private List<IIndexer> GivenIndexers(params (int DelayMs, string Title, int Score)[] indexers)
        {
            var result = indexers.Select((indexer, i) =>
            {
                var mock = new Mock<IIndexer>();
                mock.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = i + 1 });
                mock.Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                    .Returns(() => indexer.DelayMs == Timeout.Infinite
                        ? _neverAnswers.Task
                        : FetchDelayed(indexer.DelayMs, new ReleaseInfo { Title = indexer.Title, Guid = indexer.Title, Size = indexer.Score }));

                return mock.Object;
            }).ToList();

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(result);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.InteractiveSearchEnabled(true))
                  .Returns(result);

            // Score is carried in Size, titles starting with "Rejected" are rejected
            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase>((reports, criteria) => Decide(reports));

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>(), It.IsAny<bool>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase, bool>((reports, criteria, reportProgress) => Decide(reports));

            return result;
        }

        private static List<DownloadDecision> Decide(List<ReleaseInfo> reports)
        {
            return reports.Select(r =>
            {
                var remoteMovie = new RemoteMovie { Release = r, CustomFormatScore = (int)r.Size };

                return r.Title.StartsWith("Rejected")
                    ? new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.Unknown, "Rejected"))
                    : new DownloadDecision(remoteMovie);
            }).ToList();
        }

        private static async Task<IList<ReleaseInfo>> FetchDelayed(int delayMs, ReleaseInfo release)
        {
            await Task.Delay(delayMs);

            return new List<ReleaseInfo> { release };
        }

        private void GivenEarlySearchReturn(int minimumWait, int scoreThreshold, int timeout)
        {
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturn).Returns(true);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturnMinimumWait).Returns(minimumWait);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturnCustomFormatScore).Returns(scoreThreshold);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturnTimeout).Returns(timeout);
        }

        private async Task<List<string>> SearchTitles(bool interactiveSearch = false)
        {
            var decisions = await Subject.MovieSearch(_movie, true, interactiveSearch);

            return decisions.Select(d => d.RemoteMovie.Release.Title).ToList();
        }

        [Test]
        public async Task should_return_early_when_good_release_found()
        {
            GivenEarlySearchReturn(0, 10, 60);
            GivenIndexers((0, "Fast", 10), (Timeout.Infinite, "Slow", 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_wait_for_minimum_wait_before_returning_early()
        {
            GivenEarlySearchReturn(2, 10, 60);
            GivenIndexers((0, "Fast", 10), (200, "Medium", 0), (Timeout.Infinite, "Slow", 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast", "Medium");
        }

        [Test]
        public async Task should_wait_for_slow_indexer_when_no_good_release_found()
        {
            GivenEarlySearchReturn(0, 10, 60);
            GivenIndexers((0, "Fast", 5), (0, "Rejected", 100), (500, "Slow", 20));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Rejected", "Slow");
        }

        [Test]
        public async Task should_return_at_timeout_without_good_release()
        {
            GivenEarlySearchReturn(0, 10, 1);
            GivenIndexers((0, "Fast", 5), (Timeout.Infinite, "Slow", 20));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(0.5));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_keep_results_of_answered_indexers_once_timeout_passed()
        {
            GivenEarlySearchReturn(0, 10, 0);
            GivenIndexers((0, "Fast", 5), (Timeout.Infinite, "Slow", 20));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_wait_for_all_indexers_when_early_search_return_disabled()
        {
            GivenIndexers((0, "Fast", 100), (500, "Slow", 20));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }

        [Test]
        public async Task should_wait_for_all_indexers_for_interactive_search()
        {
            GivenEarlySearchReturn(0, 10, 0);
            GivenIndexers((0, "Fast", 100), (500, "Slow", 20));

            var titles = await SearchTitles(true);

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }

        [Test]
        public async Task should_cache_releases_of_early_returned_search()
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.GetMock<IConfigService>().SetupGet(s => s.AutoRedownloadFailedCacheLifetime).Returns(60);

            GivenEarlySearchReturn(0, 10, 60);
            GivenIndexers((0, "Fast", 10), (Timeout.Infinite, "Slow", 100));

            await SearchTitles();

            Subject.CachedMovieSearch(_movie.Id).Select(d => d.RemoteMovie.Release.Title).Should().BeEquivalentTo("Fast");
        }
    }
}
