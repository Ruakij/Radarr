using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class MovieSearchServiceFixture : CoreTest<MovieSearchService>
    {
        private const string QueryKey = "1:q";

        private Movie _movie;
        private List<ReleaseInfo> _releases;
        private HashSet<string> _blocklistedGuids;
        private HashSet<string> _delayedGuids;
        private Mock<IIndexer> _indexer;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie
            {
                Id = 1,
                Monitored = true,
                MinimumAvailability = MovieStatusType.Announced,
                MovieMetadata = new MovieMetadata { Title = "Movie" }
            };

            _releases = Enumerable.Range(1, 3)
                .Select(i => new ReleaseInfo { IndexerId = 1, Guid = $"guid{i}", Title = $"Movie.2024.Release{i}", DownloadProtocol = DownloadProtocol.Usenet })
                .ToList();

            _blocklistedGuids = new HashSet<string>();
            _delayedGuids = new HashSet<string>();

            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.SetConstant<ISearchForReleases>(Mocker.Resolve<ReleaseSearchService>());
            Mocker.SetConstant<IProcessDownloadDecisions>(Mocker.Resolve<ProcessDownloadDecisions>());

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.SearchResultCacheLifetime)
                  .Returns(10);

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Movie> { _movie });

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovie(_movie.Id))
                  .Returns(_movie);

            Mocker.GetMock<IMovieTranslationService>()
                  .Setup(s => s.GetAllTranslationsForMovieMetadata(It.IsAny<int>()))
                  .Returns(new List<MovieTranslation>());

            _indexer = Mocker.GetMock<IIndexer>();
            _indexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            _indexer.Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                    .Returns(() => Task.FromResult<IList<ReleaseInfo>>(_releases.ToList()));
            _indexer.Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns("q");

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _indexer.Object });

            Mocker.GetMock<IMakeDownloadDecision>()
                  .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                  .Returns<List<ReleaseInfo>, SearchCriteriaBase>((reports, criteria) => reports.Select(r => GetDecision(r, criteria.Movie)).ToList());

            Mocker.GetMock<IPrioritizeDownloadDecision>()
                  .Setup(s => s.PrioritizeDecisionsForMovies(It.IsAny<List<DownloadDecision>>()))
                  .Returns<List<DownloadDecision>>(d => d);
        }

        private DownloadDecision GetDecision(ReleaseInfo release, Movie movie)
        {
            var remoteMovie = new RemoteMovie { Release = release, Movie = movie };

            if (_blocklistedGuids.Contains(release.Guid))
            {
                return new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.Blocklisted, "Blocklisted"));
            }

            if (_delayedGuids.Contains(release.Guid))
            {
                return new DownloadDecision(remoteMovie, new DownloadRejection(DownloadRejectionReason.MinimumAgeDelay, "Delayed", RejectionType.Temporary));
            }

            return new DownloadDecision(remoteMovie);
        }

        private void SearchAndFail(string guid)
        {
            Search();

            _blocklistedGuids.Add(guid);
        }

        private void RedownloadFailed()
        {
            Subject.Execute(new MoviesSearchCommand { MovieIds = new List<int> { _movie.Id } });
        }

        private void VerifyGrabbed(string guid)
        {
            Mocker.GetMock<IDownloadService>()
                  .Verify(v => v.DownloadReport(It.Is<RemoteMovie>(r => r.Release.Guid == guid), null), Times.Once());
        }

        private void VerifySearchCount(int count)
        {
            _indexer.Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Exactly(count));
        }

        private ICached<IndexerQueryResult> GetCache()
        {
            return Mocker.Resolve<ICacheManager>().GetCache<IndexerQueryResult>(typeof(ReleaseSearchService), "indexerQueries");
        }

        private void Search()
        {
            Subject.Execute(new MoviesSearchCommand { MovieIds = new List<int> { _movie.Id } });
        }

        [Test]
        public void should_grab_next_cached_release_without_searching()
        {
            SearchAndFail("guid1");

            RedownloadFailed();

            VerifyGrabbed("guid1");
            VerifyGrabbed("guid2");
            VerifySearchCount(1);
        }

        [Test]
        public void should_skip_cached_releases_that_are_rejected_now()
        {
            SearchAndFail("guid1");
            _blocklistedGuids.Add("guid2");

            RedownloadFailed();

            VerifyGrabbed("guid3");
            Mocker.GetMock<IDownloadService>()
                  .Verify(v => v.DownloadReport(It.Is<RemoteMovie>(r => r.Release.Guid == "guid2"), null), Times.Never());
            VerifySearchCount(1);
        }

        [Test]
        public void should_search_when_cached_releases_expired()
        {
            SearchAndFail("guid1");

            var cache = GetCache();
            cache.Set(QueryKey, cache.Find(QueryKey), TimeSpan.FromMilliseconds(-1));

            RedownloadFailed();

            VerifySearchCount(2);
        }

        [Test]
        public void should_search_when_cache_is_disabled()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.SearchResultCacheLifetime)
                  .Returns(0);

            SearchAndFail("guid1");

            RedownloadFailed();

            VerifySearchCount(2);
        }

        [Test]
        public void should_grab_next_cached_release_when_link_expired()
        {
            SearchAndFail("guid1");

            Mocker.GetMock<IDownloadService>()
                  .Setup(s => s.DownloadReport(It.Is<RemoteMovie>(r => r.Release.Guid == "guid2"), It.IsAny<int?>()))
                  .ThrowsAsync(new ReleaseUnavailableException(_releases[1], "Indexer link expired"));

            RedownloadFailed();

            VerifyGrabbed("guid3");
            VerifySearchCount(1);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_search_when_cached_release_is_pending()
        {
            SearchAndFail("guid1");
            _delayedGuids.Add("guid2");
            _delayedGuids.Add("guid3");

            RedownloadFailed();

            Mocker.GetMock<IPendingReleaseService>()
                  .Verify(v => v.AddMany(It.Is<List<Tuple<DownloadDecision, PendingReleaseReason>>>(l => l.Any())), Times.Once());
            VerifySearchCount(1);
        }

        [Test]
        public void should_cache_rejected_releases()
        {
            _blocklistedGuids.Add("guid2");
            SearchAndFail("guid1");
            _blocklistedGuids.Remove("guid2");

            RedownloadFailed();

            VerifyGrabbed("guid2");
            VerifySearchCount(1);
        }

        [Test]
        public void should_clear_expired_cache_entries_when_caching()
        {
            GetCache().Set("99", new IndexerQueryResult(_releases.ToList(), DateTime.UtcNow), TimeSpan.FromMilliseconds(-1));

            SearchAndFail("guid1");

            GetCache().Count.Should().Be(1);
            GetCache().Find(QueryKey).Should().NotBeNull();
        }

        [Test]
        public void should_clear_cache_when_lifetime_is_zero()
        {
            SearchAndFail("guid1");
            GetCache().Count.Should().Be(1);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.SearchResultCacheLifetime)
                  .Returns(0);

            Search();

            GetCache().Count.Should().Be(0);
        }

        [Test]
        public void should_use_cached_releases_for_automatic_search()
        {
            SearchAndFail("guid1");

            Search();

            VerifyGrabbed("guid2");
            VerifySearchCount(1);
        }

        [Test]
        public void should_not_search_indexers_when_no_cached_release_is_acceptable()
        {
            SearchAndFail("guid1");
            _blocklistedGuids.Add("guid2");
            _blocklistedGuids.Add("guid3");

            RedownloadFailed();

            VerifySearchCount(1);
        }

        [Test]
        public void should_search_when_cache_is_disabled_for_automatic_search()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.SearchResultCacheLifetime)
                  .Returns(0);

            Search();
            Search();

            VerifySearchCount(2);
        }

        [Test]
        public void should_use_cached_releases_for_manual_search()
        {
            SearchAndFail("guid1");

            Subject.Execute(new MoviesSearchCommand { MovieIds = new List<int> { _movie.Id }, Trigger = CommandTrigger.Manual });

            VerifyGrabbed("guid2");
            VerifySearchCount(1);
        }

        [Test]
        public void should_send_only_queries_missing_from_cache()
        {
            var other = new Mock<IIndexer>();
            other.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 2 });
            other.Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns("q");
            other.Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                 .Returns(() => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            SearchAndFail("guid1");

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _indexer.Object, other.Object });

            Search();

            VerifyGrabbed("guid2");
            VerifySearchCount(1);
            other.Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Once());
        }

        [Test]
        public void should_ignore_cached_releases_of_indexers_no_longer_enabled()
        {
            var disabled = new Mock<IIndexer>();
            disabled.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 2 });
            disabled.Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns("q");
            disabled.Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                    .Returns(() => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { IndexerId = 2, Guid = "guid2", Title = "Movie.2024.Release2", DownloadProtocol = DownloadProtocol.Usenet } }));
            _releases.RemoveAt(1);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _indexer.Object, disabled.Object });

            SearchAndFail("guid1");

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _indexer.Object });

            RedownloadFailed();

            VerifyGrabbed("guid3");
            Mocker.GetMock<IDownloadService>()
                  .Verify(v => v.DownloadReport(It.Is<RemoteMovie>(r => r.Release.Guid == "guid2"), null), Times.Never());
        }
    }
}
