using System;
using System.Collections.Generic;
using System.Linq;
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

        private Mock<IIndexer> GivenCachedIndexer(int id, params string[] titles)
        {
            var indexer = new Mock<IIndexer>();
            indexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = id });
            indexer.Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                   .Returns(() => Task.FromResult<IList<ReleaseInfo>>(titles.Select(t => new ReleaseInfo { IndexerId = id, Title = t, Guid = t }).ToList()));

            return indexer;
        }

        private void GivenSearchResultCache(List<Mock<IIndexer>> automatic, List<Mock<IIndexer>> interactive)
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(15);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(automatic.Select(i => i.Object).ToList());

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.InteractiveSearchEnabled(true))
                  .Returns(interactive.Select(i => i.Object).ToList());

            Mocker.GetMock<IMakeDownloadDecision>()
                  .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                  .Returns<List<ReleaseInfo>, SearchCriteriaBase>((reports, criteria) => reports.Select(r => new DownloadDecision(new RemoteMovie { Release = r })).ToList());
        }

        private static List<string> Titles(List<DownloadDecision> decisions)
        {
            return decisions.Select(d => d.RemoteMovie.Release.Title).ToList();
        }

        [Test]
        public async Task should_serve_interactive_search_from_cache()
        {
            var indexer = GivenCachedIndexer(1, "A", "B");
            GivenSearchResultCache(new List<Mock<IIndexer>> { indexer }, new List<Mock<IIndexer>> { indexer });

            await Subject.MovieSearch(_movie, true, true);

            var cached = Subject.CachedMovieSearch(_movie.Id, true, true);

            cached.Should().NotBeNull();
            Titles(cached.Decisions).Should().BeEquivalentTo("A", "B");
            cached.SearchTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            indexer.Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Once());
        }

        [Test]
        public async Task should_always_search_indexers_when_bypassing_cache()
        {
            var indexer = GivenCachedIndexer(1, "A");
            GivenSearchResultCache(new List<Mock<IIndexer>> { indexer }, new List<Mock<IIndexer>> { indexer });

            await Subject.MovieSearch(_movie, true, true);
            await Subject.MovieSearch(_movie, true, true);

            indexer.Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Exactly(2));
        }

        [Test]
        public async Task should_not_serve_interactive_search_from_cache_missing_results_of_an_indexer()
        {
            var automatic = GivenCachedIndexer(1, "A");
            var interactiveOnly = GivenCachedIndexer(2, "B");
            GivenSearchResultCache(new List<Mock<IIndexer>> { automatic }, new List<Mock<IIndexer>> { automatic, interactiveOnly });

            await Subject.MovieSearch(_movie, false, false);

            Subject.CachedMovieSearch(_movie.Id, true, true).Should().BeNull();
            Titles(Subject.CachedMovieSearch(_movie.Id, false, false).Decisions).Should().BeEquivalentTo("A");
        }

        [Test]
        public async Task should_not_serve_results_of_interactive_only_indexers_to_automatic_search()
        {
            var automatic = GivenCachedIndexer(1, "A");
            var interactiveOnly = GivenCachedIndexer(2, "B");
            GivenSearchResultCache(new List<Mock<IIndexer>> { automatic }, new List<Mock<IIndexer>> { automatic, interactiveOnly });

            await Subject.MovieSearch(_movie, true, true);

            Titles(Subject.CachedMovieSearch(_movie.Id, false, false).Decisions).Should().BeEquivalentTo("A");
            Titles(Subject.CachedMovieSearch(_movie.Id, true, true).Decisions).Should().BeEquivalentTo("A", "B");
        }

        [Test]
        public async Task should_not_serve_expired_search_results()
        {
            var indexer = GivenCachedIndexer(1, "A");
            GivenSearchResultCache(new List<Mock<IIndexer>> { indexer }, new List<Mock<IIndexer>> { indexer });

            await Subject.MovieSearch(_movie, true, true);

            var cache = Mocker.Resolve<ICacheManager>().GetCache<SearchResultCacheEntry>(typeof(ReleaseSearchService), "searchResults");
            cache.Set(_movie.Id.ToString(), cache.Find(_movie.Id.ToString()), TimeSpan.FromMilliseconds(-1));

            Subject.CachedMovieSearch(_movie.Id, true, true).Should().BeNull();
        }

        [Test]
        public async Task should_not_serve_search_results_when_cache_is_disabled()
        {
            var indexer = GivenCachedIndexer(1, "A");
            GivenSearchResultCache(new List<Mock<IIndexer>> { indexer }, new List<Mock<IIndexer>> { indexer });

            await Subject.MovieSearch(_movie, true, true);

            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(0);

            Subject.CachedMovieSearch(_movie.Id, true, true).Should().BeNull();
        }
    }
}
