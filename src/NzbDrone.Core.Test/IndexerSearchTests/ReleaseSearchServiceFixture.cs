using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
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
            _mockIndexer.Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns("q");

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
                mock.Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns("q");
                mock.Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                    .Returns(() => indexer.DelayMs == Timeout.Infinite
                        ? _neverAnswers.Task
                        : FetchDelayed(indexer.DelayMs, new ReleaseInfo { IndexerId = i + 1, Title = indexer.Title, Guid = indexer.Title }));

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

            return Titles(decisions);
        }

        private Mock<IIndexer> GivenCachedIndexer(int id, params string[] titles)
        {
            var indexer = new Mock<IIndexer>();
            indexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = id });
            indexer.Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns("q");
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
        public async Task should_count_minimum_wait_from_search_start_across_priority_groups()
        {
            GivenEarlySearchReturn(2);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchIndexersInPriorityOrder).Returns(true);
            var indexers = GivenIndexers((1500, "First", Quality.SDTV, 0), (0, "Fast", Quality.Bluray1080p, 10), (Timeout.Infinite, "Slow", Quality.Bluray2160p, 100));
            ((IndexerDefinition)indexers[0].Definition).Priority = 1;
            ((IndexerDefinition)indexers[1].Definition).Priority = 2;
            ((IndexerDefinition)indexers[2].Definition).Priority = 2;

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
            titles.Should().BeEquivalentTo("First", "Fast");
        }

        private List<IIndexer> GivenPriorityGroups(params (int Priority, string Title, Quality Quality, int Score)[] indexers)
        {
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchIndexersInPriorityOrder).Returns(true);

            var result = GivenIndexers(indexers.Select(i => (0, i.Title, i.Quality, i.Score)).ToArray());

            for (var i = 0; i < indexers.Length; i++)
            {
                ((IndexerDefinition)result[i].Definition).Priority = indexers[i].Priority;
            }

            return result;
        }

        [Test]
        public async Task should_search_all_priorities_when_priority_order_disabled()
        {
            GivenEarlySearchReturn(0);
            GivenPriorityGroups((1, "Good", Quality.Bluray1080p, 10), (2, "Other", Quality.SDTV, 0));
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchIndexersInPriorityOrder).Returns(false);

            var titles = await SearchTitles(true);

            titles.Should().BeEquivalentTo("Good", "Other");
        }

        [Test]
        public async Task should_stop_after_first_priority_group_with_good_enough_release()
        {
            GivenEarlySearchReturn(0);
            var indexers = GivenPriorityGroups((1, "Good", Quality.Bluray1080p, 10), (1, "Same", Quality.SDTV, 0), (2, "Lower", Quality.Bluray2160p, 100));

            var titles = await SearchTitles(true);

            titles.Should().BeEquivalentTo("Good", "Same");
            Mock.Get(indexers[2]).Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Never());
        }

        [Test]
        public async Task should_search_next_priority_group_when_nothing_good_enough_found()
        {
            GivenEarlySearchReturn(0);
            var indexers = GivenPriorityGroups((1, "Rejected", Quality.Bluray1080p, 10), (2, "Lower", Quality.SDTV, 0), (3, "Lowest", Quality.Bluray1080p, 10), (4, "Last", Quality.Bluray1080p, 10));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Rejected", "Lower", "Lowest");
            Mock.Get(indexers[3]).Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Never());
        }

        [Test]
        public async Task should_search_indexers_up_to_required_priority_as_first_group()
        {
            GivenEarlySearchReturn(0);
            GivenRequiredPriority(10);
            var indexers = GivenPriorityGroups((1, "Good", Quality.Bluray1080p, 10), (10, "Required", Quality.SDTV, 0), (11, "Lower", Quality.Bluray2160p, 100));

            var titles = await SearchTitles(true);

            titles.Should().BeEquivalentTo("Good", "Required");
            Mock.Get(indexers[2]).Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Never());
        }

        private static List<IndexerSearchStatusType> Statuses(InteractiveSearchResult result)
        {
            return result.Status.Indexers.OrderBy(s => s.IndexerId).Select(s => s.Status).ToList();
        }

        [Test]
        public async Task should_report_skipped_indexers_and_search_them_when_searching_remaining()
        {
            GivenQueryCache();
            GivenEarlySearchReturn(0);
            var indexers = GivenPriorityGroups((1, "Good", Quality.Bluray1080p, 10), (2, "Lower", Quality.SDTV, 0));

            var first = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Statuses(first).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Skipped);
            first.Status.Indexers[0].ReleaseCount.Should().Be(1);
            Subject.GetInteractiveSearchStatus(_movie.Id).Should().BeEquivalentTo(first.Status, o => o.Excluding(s => s.Path.EndsWith("History")));

            var remaining = await Subject.InteractiveMovieSearch(_movie.Id, false, true);

            Titles(remaining.Decisions).Should().BeEquivalentTo("Good", "Lower");
            Statuses(remaining).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Searched);
            Mock.Get(indexers[0]).Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Once());
            Mock.Get(indexers[1]).Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Once());
        }

        [Test]
        public async Task should_report_failed_and_timed_out_indexers_and_search_them_again_when_searching_remaining()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0), (0, "C", Quality.SDTV, 0));
            Mock.Get(indexers[1]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>())).ThrowsAsync(new Exception("Indexer failed"));
            Mock.Get(indexers[2]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>())).ThrowsAsync(new WebException("Http request timed out", WebExceptionStatus.Timeout));

            var first = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Statuses(first).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Failed, IndexerSearchStatusType.TimedOut);
            first.Status.Indexers.Single(s => s.IndexerId == 2).Message.Should().Be("Indexer failed");

            await Subject.InteractiveMovieSearch(_movie.Id, false, true);

            Mock.Get(indexers[0]).Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Once());
            Mock.Get(indexers[1]).Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Exactly(2));
            ExceptionVerification.ExpectedErrors(4);
        }

        [Test]
        public async Task should_report_response_times_of_searched_and_failed_indexers()
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            var indexers = GivenIndexers((100, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0));
            Mock.Get(indexers[1]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                .Callback<MovieSearchCriteria>(c =>
                {
                    c.AddRequestDuration(2, TimeSpan.FromMilliseconds(10));
                    c.AddRequestDuration(2, TimeSpan.FromMilliseconds(20));
                    c.AddRequestDuration(2, TimeSpan.FromMilliseconds(60));
                })
                .ThrowsAsync(new Exception("Indexer failed"));

            await Subject.InteractiveMovieSearch(_movie.Id, false, false);
            await Subject.InteractiveMovieSearch(_movie.Id, true, false);

            var status = Subject.GetInteractiveSearchStatus(_movie.Id).Indexers.OrderBy(s => s.IndexerId).ToList();

            status[0].QueryCount.Should().Be(1);
            status[0].MedianResponseMs.Should().BeGreaterOrEqualTo(90);
            status[0].History.Count.Should().Be(2);
            status[0].History.LowMs.Should().BeGreaterOrEqualTo(90);

            // Failed queries count for the search but not for the history, every request counts
            status[1].QueryCount.Should().Be(3);
            status[1].MedianResponseMs.Should().Be(20);
            status[1].History.Should().BeNull();
            ExceptionVerification.ExpectedErrors(2);
        }

        [Test]
        public async Task should_leave_out_indexers_without_a_query_for_the_movie()
        {
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0));
            Mock.Get(indexers[1]).Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns((string)null);

            var result = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Titles(result.Decisions).Should().BeEquivalentTo("A");
            result.Status.Indexers.Select(s => s.IndexerId).Should().Equal(1);
            VerifyFetched(indexers[1], 0);
        }

        [Test]
        public async Task should_send_nothing_when_only_indexers_without_a_query_are_not_cached()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0));
            Mock.Get(indexers[1]).Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns((string)null);
            GivenCachedQuery(1, "A", DateTime.UtcNow.AddMinutes(-5));

            (await SearchTitles()).Should().BeEquivalentTo("A");
            VerifyFetched(indexers[0], 0);
            VerifyFetched(indexers[1], 0);
        }

        [Test]
        public async Task should_report_cached_indexers()
        {
            var indexer = GivenCachedIndexer(1, "A", "B");
            GivenSearchResultCache(new List<Mock<IIndexer>> { indexer }, new List<Mock<IIndexer>> { indexer });

            await Subject.InteractiveMovieSearch(_movie.Id, false, false);
            var cached = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Titles(cached.Decisions).Should().BeEquivalentTo("A", "B");
            Statuses(cached).Should().Equal(IndexerSearchStatusType.Cached);
            cached.Status.Indexers[0].ReleaseCount.Should().Be(2);
            cached.Status.CachedAt.Should().NotBeNull();
            cached.Status.Indexers[0].CachedAt.Should().Be(cached.Status.CachedAt);

            var again = await Subject.InteractiveMovieSearch(_movie.Id, true, false);

            Statuses(again).Should().Equal(IndexerSearchStatusType.Searched);
            again.Status.CachedAt.Should().BeNull();
            indexer.Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Exactly(2));
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

        private void GivenQueryCache()
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(15);
        }

        private ICached<IndexerQueryResult> QueryCache => Mocker.Resolve<ICacheManager>().GetCache<IndexerQueryResult>(typeof(ReleaseSearchService), "indexerQueries");

        private void GivenCachedQuery(int indexerId, string title, DateTime fetchedAt)
        {
            QueryCache.Set($"{indexerId}:q", new IndexerQueryResult(new List<ReleaseInfo> { new ReleaseInfo { IndexerId = indexerId, Title = title, Guid = title } }, fetchedAt), TimeSpan.FromMinutes(60));
        }

        private static void VerifyFetched(IIndexer indexer, int times)
        {
            Mock.Get(indexer).Verify(v => v.Fetch(It.IsAny<MovieSearchCriteria>()), Times.Exactly(times));
        }

        [Test]
        public async Task should_serve_query_from_cache_until_its_requests_change()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0));

            (await SearchTitles()).Should().BeEquivalentTo("A");
            (await SearchTitles()).Should().BeEquivalentTo("A");
            VerifyFetched(indexers[0], 1);

            Mock.Get(indexers[0]).Setup(s => s.GetSearchQueryKey(It.IsAny<MovieSearchCriteria>())).Returns("other");

            (await SearchTitles()).Should().BeEquivalentTo("A");
            VerifyFetched(indexers[0], 2);
        }

        [Test]
        public async Task should_not_cache_queries_when_cache_is_disabled()
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0));

            await SearchTitles();
            await SearchTitles();

            VerifyFetched(indexers[0], 2);
        }

        [Test]
        public async Task should_share_cached_query_between_searches_with_the_same_requests()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0));

            await SearchTitles();
            var interactive = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Titles(interactive.Decisions).Should().BeEquivalentTo("A");
            Statuses(interactive).Should().Equal(IndexerSearchStatusType.Cached);
            VerifyFetched(indexers[0], 1);
        }

        [Test]
        public async Task should_send_only_queries_missing_from_cache()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0));
            var fetchedAt = DateTime.UtcNow.AddMinutes(-5);
            GivenCachedQuery(1, "A", fetchedAt);

            var result = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Titles(result.Decisions).Should().BeEquivalentTo("A", "B");
            VerifyFetched(indexers[0], 0);
            VerifyFetched(indexers[1], 1);

            var status = result.Status.Indexers.OrderBy(s => s.IndexerId).ToList();
            Statuses(result).Should().Equal(IndexerSearchStatusType.Cached, IndexerSearchStatusType.Searched);
            status[0].CachedAt.Should().Be(fetchedAt);
            status[0].QueryCount.Should().BeNull();
            status[1].CachedAt.Should().BeNull();
            status[1].QueryCount.Should().Be(1);
            result.Status.CachedAt.Should().Be(fetchedAt);
        }

        [Test]
        public async Task should_report_oldest_cached_query()
        {
            GivenQueryCache();
            GivenIndexers((0, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0));
            var older = DateTime.UtcNow.AddMinutes(-10);
            GivenCachedQuery(1, "A", DateTime.UtcNow.AddMinutes(-2));
            GivenCachedQuery(2, "B", older);

            var result = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Statuses(result).Should().Equal(IndexerSearchStatusType.Cached, IndexerSearchStatusType.Cached);
            result.Status.CachedAt.Should().Be(older);
        }

        [Test]
        public async Task should_expire_each_query_at_its_own_fetch_time()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0));
            var recent = DateTime.UtcNow.AddMinutes(-5);
            GivenCachedQuery(1, "A", DateTime.UtcNow.AddMinutes(-20));
            GivenCachedQuery(2, "B", recent);

            (await SearchTitles()).Should().BeEquivalentTo("A", "B");
            (await SearchTitles()).Should().BeEquivalentTo("A", "B");

            VerifyFetched(indexers[0], 1);
            VerifyFetched(indexers[1], 0);

            // A hit does not extend the lifetime of the query
            QueryCache.Find("2:q").FetchedAt.Should().Be(recent);
        }

        [Test]
        public async Task should_not_cache_failed_queries()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0));
            Mock.Get(indexers[0]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>())).ThrowsAsync(new Exception("Indexer failed"));
            Mock.Get(indexers[1]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                .Callback<MovieSearchCriteria>(c => c.IndexerFailures.TryAdd(2, new WebException("Http request timed out", WebExceptionStatus.Timeout)))
                .ReturnsAsync(new List<ReleaseInfo>());

            await SearchTitles();
            await SearchTitles();

            VerifyFetched(indexers[0], 2);
            VerifyFetched(indexers[1], 2);
            ExceptionVerification.ExpectedErrors(2);
        }

        [Test]
        public async Task should_cache_empty_result()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0));
            Mock.Get(indexers[0]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>())).ReturnsAsync(new List<ReleaseInfo>());

            await SearchTitles();
            var result = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            result.Decisions.Should().BeEmpty();
            Statuses(result).Should().Equal(IndexerSearchStatusType.Cached);
            VerifyFetched(indexers[0], 1);
        }

        [Test]
        public async Task should_cache_query_answered_after_early_return()
        {
            GivenQueryCache();
            GivenEarlySearchReturn(0);
            var indexers = GivenIndexers((0, "Fast", Quality.Bluray1080p, 10), (0, "Slow", Quality.Bluray2160p, 100));
            var slow = new TaskCompletionSource<IList<ReleaseInfo>>();
            Mock.Get(indexers[1]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>())).Returns(slow.Task);

            var decisions = await Subject.MovieSearch(_movie, true, false);

            slow.SetResult(new List<ReleaseInfo> { new ReleaseInfo { IndexerId = 2, Title = "Slow", Guid = "Slow" } });

            Titles(decisions).Should().BeEquivalentTo("Fast");

            var result = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Titles(result.Decisions).Should().BeEquivalentTo("Fast", "Slow");
            Statuses(result).Should().Equal(IndexerSearchStatusType.Cached, IndexerSearchStatusType.Cached);
            VerifyFetched(indexers[1], 1);
        }

        [Test]
        public async Task should_overwrite_cached_queries_on_search_again()
        {
            GivenQueryCache();
            var indexers = GivenIndexers((0, "A", Quality.SDTV, 0), (0, "B", Quality.SDTV, 0));

            await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            _releases["A2"] = (Quality.SDTV, 0);
            Mock.Get(indexers[0]).Setup(s => s.Fetch(It.IsAny<MovieSearchCriteria>()))
                .ReturnsAsync(new List<ReleaseInfo> { new ReleaseInfo { IndexerId = 1, Title = "A2", Guid = "A2" } });

            var again = await Subject.InteractiveMovieSearch(_movie.Id, true, false);

            Titles(again.Decisions).Should().BeEquivalentTo("A2", "B");
            Statuses(again).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Searched);
            again.Status.CachedAt.Should().BeNull();

            var cached = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Titles(cached.Decisions).Should().BeEquivalentTo("A2", "B");
            Statuses(cached).Should().Equal(IndexerSearchStatusType.Cached, IndexerSearchStatusType.Cached);
            VerifyFetched(indexers[0], 2);
            VerifyFetched(indexers[1], 2);
        }

        [Test]
        public async Task should_take_cached_queries_of_priority_groups_the_search_did_not_reach()
        {
            GivenQueryCache();
            GivenEarlySearchReturn(0);
            var indexers = GivenPriorityGroups((1, "Good", Quality.Bluray1080p, 10), (2, "Lower", Quality.SDTV, 0), (2, "Other", Quality.SDTV, 0));
            GivenCachedQuery(2, "Lower", DateTime.UtcNow.AddMinutes(-1));

            var result = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Titles(result.Decisions).Should().BeEquivalentTo("Good", "Lower");
            Statuses(result).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Cached, IndexerSearchStatusType.Skipped);
            VerifyFetched(indexers[1], 0);
            VerifyFetched(indexers[2], 0);
        }

        [Test]
        public async Task should_send_only_queries_missing_from_cache_when_searching_remaining()
        {
            GivenQueryCache();
            GivenEarlySearchReturn(0);
            var indexers = GivenPriorityGroups((1, "Good", Quality.Bluray1080p, 10), (2, "Lower", Quality.SDTV, 0), (2, "Other", Quality.SDTV, 0));

            var first = await Subject.InteractiveMovieSearch(_movie.Id, false, false);

            Statuses(first).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Skipped, IndexerSearchStatusType.Skipped);

            // Another search answered the query in the meantime
            GivenCachedQuery(2, "Lower", DateTime.UtcNow.AddMinutes(-1));

            var remaining = await Subject.InteractiveMovieSearch(_movie.Id, false, true);

            Titles(remaining.Decisions).Should().BeEquivalentTo("Good", "Lower", "Other");
            Statuses(remaining).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Cached, IndexerSearchStatusType.Searched);
            VerifyFetched(indexers[0], 1);
            VerifyFetched(indexers[1], 0);
            VerifyFetched(indexers[2], 1);
        }
    }
}
