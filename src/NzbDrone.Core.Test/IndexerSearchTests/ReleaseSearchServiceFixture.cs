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
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
        public class ReleaseSearchServiceFixture : CoreTest<ReleaseSearchService>
    {
        private readonly CustomFormat _goodFormat = new CustomFormat("Good") { Id = 1 };
        private readonly Dictionary<string, (Quality Quality, int Score)> _releases = new Dictionary<string, (Quality Quality, int Score)>();

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
            _releases.Clear();

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .Build();

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            Mocker.GetMock<IMovieTranslationService>()
                .Setup(s => s.GetAllTranslationsForMovieMetadata(It.IsAny<int>()))
                .Returns(new List<MovieTranslation>());

            GivenProfile();
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

        private void GivenProfile(bool upgradeAllowed = true)
        {
            Mocker.SetConstant<IUpgradableSpecification>(Mocker.Resolve<UpgradableSpecification>());

            _movie.QualityProfile = new QualityProfile
            {
                UpgradeAllowed = upgradeAllowed,
                Cutoff = Quality.Bluray1080p.Id,
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                FormatItems = new List<ProfileFormatItem> { new ProfileFormatItem { Format = _goodFormat, Score = 10 } },
                CutoffFormatScore = 10
            };
        }

        private List<IIndexer> GivenIndexers(params (int DelayMs, string Title, Quality Quality, int Score)[] indexers)
        {
            var result = indexers.Select((indexer, i) =>
            {
                var mock = new Mock<IIndexer>();
                mock.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = i + 1 });
                mock.Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                    .Returns(() => indexer.DelayMs == Timeout.Infinite
                        ? _neverAnswers.Task
                        : FetchDelayed(indexer.DelayMs, new ReleaseInfo { Title = indexer.Title, Guid = indexer.Title }));

                _releases[indexer.Title] = (indexer.Quality, indexer.Score);

                return mock.Object;
            }).ToList();

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(result);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.InteractiveSearchEnabled(true))
                  .Returns(result);

            // Titles starting with "Rejected" are rejected
            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase>((reports, criteria) => Decide(reports));

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>(), It.IsAny<bool>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase, bool>((reports, criteria, reportProgress) => Decide(reports));

            return result;
        }

        private List<DownloadDecision> Decide(List<ReleaseInfo> reports)
        {
            return reports.Select(r =>
            {
                var (quality, score) = _releases[r.Title];
                var remoteMovie = new RemoteMovie
                {
                    Release = r,
                    ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(quality) },
                    CustomFormats = score >= 10 ? new List<CustomFormat> { _goodFormat } : new List<CustomFormat>(),
                    CustomFormatScore = score
                };

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

        private void GivenEarlySearchReturn(int minimumWait)
        {
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturn).Returns(true);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturnMinimumWait).Returns(minimumWait);
        }

        private async Task<List<string>> SearchTitles(bool interactiveSearch = false)
        {
            var decisions = await Subject.MovieSearch(_movie, true, interactiveSearch);

            return decisions.Select(d => d.RemoteMovie.Release.Title).ToList();
        }

        [Test]
        public async Task should_return_early_when_release_meets_cutoffs()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", Quality.Bluray1080p, 10), (Timeout.Infinite, "Slow", Quality.Bluray2160p, 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_return_early_when_profile_does_not_allow_upgrades()
        {
            GivenProfile(upgradeAllowed: false);
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", Quality.SDTV, 0), (Timeout.Infinite, "Slow", Quality.Bluray2160p, 100));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_wait_for_minimum_wait_before_returning_early()
        {
            GivenEarlySearchReturn(2);
            GivenIndexers((0, "Fast", Quality.Bluray1080p, 10), (200, "Medium", Quality.SDTV, 0), (Timeout.Infinite, "Slow", Quality.Bluray2160p, 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast", "Medium");
        }

        [Test]
        public async Task should_wait_for_slow_indexer_when_quality_cutoff_not_met()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", Quality.HDTV720p, 100), (500, "Slow", Quality.Bluray1080p, 10));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }

        [Test]
        public async Task should_wait_for_slow_indexer_when_custom_format_cutoff_not_met()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", Quality.Bluray1080p, 0), (500, "Slow", Quality.Bluray1080p, 10));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }

        [Test]
        public async Task should_wait_for_slow_indexer_when_only_rejected_release_meets_cutoffs()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Rejected", Quality.Bluray1080p, 100), (500, "Slow", Quality.HDTV720p, 0));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Rejected", "Slow");
        }

        private void GivenRequiredPriority(int requiredPriority)
        {
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturnRequiredPriority).Returns(requiredPriority);
        }

        [Test]
        public async Task should_not_wait_for_preferred_indexer_when_required_priority_disabled()
        {
            GivenEarlySearchReturn(0);
            GivenRequiredPriority(0);
            var indexers = GivenIndexers((0, "Fast", Quality.Bluray1080p, 10), (Timeout.Infinite, "Slow", Quality.Bluray2160p, 100));
            ((IndexerDefinition)indexers[1].Definition).Priority = 1;

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_wait_for_slow_indexer_with_required_priority()
        {
            GivenEarlySearchReturn(0);
            GivenRequiredPriority(10);
            var indexers = GivenIndexers((0, "Fast", Quality.Bluray1080p, 10), (500, "Required", Quality.SDTV, 0), (Timeout.Infinite, "Slow", Quality.Bluray2160p, 100));
            ((IndexerDefinition)indexers[1].Definition).Priority = 10;

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast", "Required");
        }

        [Test]
        public async Task should_return_early_when_indexer_with_required_priority_fails()
        {
            GivenEarlySearchReturn(0);
            GivenRequiredPriority(10);
            var indexers = GivenIndexers((0, "Fast", Quality.Bluray1080p, 10), (0, "Required", Quality.SDTV, 0), (Timeout.Infinite, "Slow", Quality.Bluray2160p, 100));
            ((IndexerDefinition)indexers[1].Definition).Priority = 5;
            Mock.Get(indexers[1]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>())).ThrowsAsync(new Exception("Indexer failed"));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast");
            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public async Task should_wait_for_all_indexers_when_early_search_return_disabled()
        {
            GivenIndexers((0, "Fast", Quality.Bluray1080p, 100), (500, "Slow", Quality.SDTV, 0));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }

        [Test]
        public async Task should_wait_for_all_indexers_for_interactive_search()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", Quality.Bluray1080p, 100), (500, "Slow", Quality.SDTV, 0));

            var titles = await SearchTitles(true);

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }
    }
}
