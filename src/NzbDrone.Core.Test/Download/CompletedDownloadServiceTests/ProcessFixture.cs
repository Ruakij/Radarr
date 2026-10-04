using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.AlternativeTitles;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download.CompletedDownloadServiceTests
{
    [TestFixture]
    public class ProcessFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var completed = Builder<DownloadClientItem>.CreateNew()
                                                    .With(h => h.Status = DownloadItemStatus.Completed)
                                                    .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                    .With(h => h.Title = "Drone.S01E01.HDTV")
                                                    .Build();

            var remoteMovie = BuildRemoteMovie();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                    .With(c => c.State = TrackedDownloadState.Downloading)
                    .With(c => c.DownloadItem = completed)
                    .With(c => c.RemoteMovie = remoteMovie)
                    .Build();

            Mocker.GetMock<IDownloadClient>()
              .SetupGet(c => c.Definition)
              .Returns(new DownloadClientDefinition { Id = 1, Name = "testClient" });

            Mocker.GetMock<IProvideDownloadClient>()
                  .Setup(c => c.Get(It.IsAny<int>()))
                  .Returns(Mocker.GetMock<IDownloadClient>().Object);

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(c => c.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns((DownloadClientItem item, DownloadClientItem previous) => item);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(_trackedDownload.DownloadItem.DownloadId))
                  .Returns(new List<MovieHistory>());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie("Drone.S01E01.HDTV"))
                  .Returns(remoteMovie.Movie);
        }

        private RemoteMovie BuildRemoteMovie()
        {
            return new RemoteMovie
            {
                Movie = new Movie(),
            };
        }

        private void GivenNoGrabbedHistory()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(_trackedDownload.DownloadItem.DownloadId))
                .Returns(new List<MovieHistory>());
        }

        private void GivenMovieMatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie(It.IsAny<string>()))
                  .Returns(_trackedDownload.RemoteMovie.Movie);
        }

        private void GivenABadlyNamedDownload()
        {
            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = "Droned Pilot"; // Set a badly named download
            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.Is<string>(i => i == "1234")))
                  .Returns(new List<MovieHistory>
                  {
                      new MovieHistory() { SourceTitle = "Droned S01E01", EventType = MovieHistoryEventType.Grabbed }
                  });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie(It.IsAny<string>()))
                  .Returns((Movie)null);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie("Droned S01E01"))
                  .Returns(BuildRemoteMovie().Movie);
        }

        private void GivenGrabbedByIdMatch(string releaseTitle, string title, int year, string alternativeTitle = null, string translation = null, int? secondaryYear = null, MovieMatchType matchType = MovieMatchType.Id, ReleaseSourceType releaseSource = ReleaseSourceType.Search)
        {
            var movie = new Movie { Id = 10, MovieMetadataId = 20 };
            movie.MovieMetadata.Value.Title = title;
            movie.MovieMetadata.Value.Year = year;
            movie.MovieMetadata.Value.SecondaryYear = secondaryYear;

            if (alternativeTitle != null)
            {
                movie.MovieMetadata.Value.AlternativeTitles.Add(new AlternativeTitle(alternativeTitle));
            }

            Mocker.GetMock<IMovieTranslationService>()
                  .Setup(s => s.GetAllTranslationsForMovieMetadata(movie.MovieMetadataId))
                  .Returns(translation == null ? new List<MovieTranslation>() : new List<MovieTranslation> { new MovieTranslation { Title = translation } });

            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = releaseTitle;

            var history = new MovieHistory { MovieId = movie.Id, SourceTitle = releaseTitle, EventType = MovieHistoryEventType.Grabbed };
            history.Data[MovieHistory.MOVIE_MATCH_TYPE] = matchType.ToString();
            history.Data[MovieHistory.RELEASE_SOURCE] = releaseSource.ToString();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId("1234"))
                  .Returns(new List<MovieHistory> { history });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie(It.IsAny<string>()))
                  .Returns((Movie)null);

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovie(movie.Id))
                  .Returns(movie);
        }

        private void GivenMinimumTitleSimilarity(int minimumTitleSimilarity)
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.MinimumTitleSimilarity)
                  .Returns(minimumTitleSimilarity);
        }

        [Test]
        public void should_not_process_id_matched_release_when_minimum_title_similarity_is_disabled()
        {
            GivenGrabbedByIdMatch("Dead.Poets.Society.1989.1080p.BluRay.x264-GRP", "Dead Poets Society", 1989);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [TestCase("Dead.Poets.Society.1989.1080p.BluRay.x264-GRP", "Dead Poets Society", 1989, null, null)]
        [TestCase("Ziemlich.beste.Freunde.2011.German.1080p.BluRay.x264-GRP", "The Intouchables", 2011, null, "Ziemlich beste Freunde")]
        [TestCase("Sen.to.Chihiro.no.Kamikakushi.2001.JAPANESE.1080p.BluRay.x264-GRP", "Spirited Away", 2001, "Sen to Chihiro no Kamikakushi", null)]
        [TestCase("Mission.Impossible.2023.1080p.WEB-DL.DDP5.1.H.264-GRP", "Mission: Impossible - Dead Reckoning Part One", 2023, null, null)]
        [TestCase("Dead.Poets.Society.1990.1080p.BluRay.x264-GRP", "Dead Poets Society", 1989, null, null)]
        [TestCase("Fast.and.Furious.2009.1080p.BluRay.x264-GRP", "Fast & Furious", 2009, null, null)]
        [TestCase("Spider.Man.2002.1080p.BluRay.x264-GRP", "Spider-Man", 2002, null, null)]
        [TestCase("X.Men.2000.1080p.BluRay.x264-GRP", "X-Men", 2000, null, null)]
        public void should_process_id_matched_release_with_similar_title(string releaseTitle, string title, int year, string alternativeTitle, string translation)
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch(releaseTitle, title, year, alternativeTitle, translation);

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
        }

        [TestCase("Night.of.the.Living.Dead.1968.1080p.BluRay.x264-GRP", "Night of the Living Dead", 1990)]
        [TestCase("It.2009.1080p.BluRay.x264-GRP", "Up", 2009)]
        public void should_not_process_id_matched_release_with_dissimilar_title_or_year(string releaseTitle, string title, int year)
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch(releaseTitle, title, year);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_process_id_matched_release_with_year_matching_secondary_year()
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch("Dead.Poets.Society.1995.1080p.BluRay.x264-GRP", "Dead Poets Society", 1989, secondaryYear: 1994);

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
        }

        [Test]
        public void should_not_process_id_matched_release_without_year()
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch("Dead.Poets.Society.1080p.BluRay.x264-GRP", "Dead Poets Society", 1989);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        // "Dead Poets" vs "Dead Poets Society" is exactly 72% similar
        [TestCase(72, true)]
        [TestCase(73, false)]
        public void should_compare_title_similarity_against_minimum_inclusively(int minimumTitleSimilarity, bool expectedImport)
        {
            GivenMinimumTitleSimilarity(minimumTitleSimilarity);
            GivenGrabbedByIdMatch("Dead.Poets.1989.1080p.BluRay.x264-GRP", "Dead Poets Society", 1989);

            Subject.Check(_trackedDownload);

            _trackedDownload.State.Should().Be(expectedImport ? TrackedDownloadState.ImportPending : TrackedDownloadState.ImportBlocked);
        }

        [TestCase(MovieMatchType.Id, ReleaseSourceType.InteractiveSearch)]
        [TestCase(MovieMatchType.Title, ReleaseSourceType.Search)]
        public void should_process_release_not_matched_by_id_or_grabbed_interactively_without_title_similarity_check(MovieMatchType matchType, ReleaseSourceType releaseSource)
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch("It.2009.1080p.BluRay.x264-GRP", "Up", 2009, matchType: matchType, releaseSource: releaseSource);

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
            Mocker.GetMock<IMovieTranslationService>()
                  .Verify(s => s.GetAllTranslationsForMovieMetadata(It.IsAny<int>()), Times.Never());
        }

        [TestCase(DownloadItemStatus.Downloading)]
        [TestCase(DownloadItemStatus.Failed)]
        [TestCase(DownloadItemStatus.Queued)]
        [TestCase(DownloadItemStatus.Paused)]
        [TestCase(DownloadItemStatus.Warning)]
        public void should_not_process_if_download_status_isnt_completed(DownloadItemStatus status)
        {
            _trackedDownload.DownloadItem.Status = status;

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_if_matching_history_is_not_found_and_no_category_specified()
        {
            _trackedDownload.DownloadItem.Category = null;
            GivenNoGrabbedHistory();

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_process_if_matching_history_is_not_found_but_category_specified()
        {
            _trackedDownload.DownloadItem.Category = "tv";
            GivenNoGrabbedHistory();
            GivenMovieMatch();

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
        }

        [Test]
        public void should_not_process_if_output_path_is_empty()
        {
            _trackedDownload.DownloadItem.OutputPath = default(OsPath);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_if_the_download_cannot_be_tracked_using_the_source_title_as_it_was_initiated_externally()
        {
            GivenABadlyNamedDownload();

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv" }))
                           });

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_when_there_is_a_title_mismatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie("Drone.S01E01.HDTV"))
                  .Returns((Movie)null);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        private void AssertNotReadyToImport()
        {
            _trackedDownload.State.Should().NotBe(TrackedDownloadState.ImportPending);
        }

        private void AssertReadyToImport()
        {
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
        }
    }
}
