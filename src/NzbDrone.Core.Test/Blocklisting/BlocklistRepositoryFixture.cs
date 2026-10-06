using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Blocklisting
{
    [TestFixture]
    public class BlocklistRepositoryFixture : DbTest<BlocklistRepository, Blocklist>
    {
        private Blocklist _blocklist;
        private Movie _movie1;
        private Movie _movie2;

        [SetUp]
        public void Setup()
        {
            _blocklist = new Blocklist
            {
                MovieId = 1234,
                Quality = new QualityModel(),
                Languages = new List<Language>(),
                SourceTitle = "movie.title.1998",
                Date = DateTime.UtcNow
            };

            _movie1 = Builder<Movie>.CreateNew()
                         .With(s => s.Id = 7)
                         .Build();

            _movie2 = Builder<Movie>.CreateNew()
                                     .With(s => s.Id = 8)
                                     .Build();
        }

        [Test]
        public void should_be_able_to_write_to_database()
        {
            Subject.Insert(_blocklist);
            Subject.All().Should().HaveCount(1);
        }

        [Test]
        public void should_should_have_movie_id()
        {
            Subject.Insert(_blocklist);

            Subject.All().First().MovieId.Should().Be(_blocklist.MovieId);
        }

        [Test]
        public void should_check_for_blocklisted_title_case_insensative()
        {
            Subject.Insert(_blocklist);

            Subject.BlocklistedByTitle(_blocklist.MovieId, _blocklist.SourceTitle.ToUpperInvariant()).Should().HaveCount(1);
        }

        [Test]
        public void should_delete_blocklists_by_movieId()
        {
            var blocklistItems = Builder<Blocklist>.CreateListOfSize(5)
                .TheFirst(1)
                .With(c => c.MovieId = _movie2.Id)
                .TheRest()
                .With(c => c.MovieId = _movie1.Id)
                .All()
                .With(c => c.Quality = new QualityModel())
                .With(c => c.Languages = new List<Language>())
                .With(c => c.Id = 0)
                .BuildListOfNew();

            Db.InsertMany(blocklistItems);

            Subject.DeleteForMovies(new List<int> { _movie1.Id });

            var blocklist = Subject.All();
            var removedMovieBlocklists = blocklist.Where(b => b.MovieId == _movie1.Id);
            var nonRemovedMovieBlocklists = blocklist.Where(b => b.MovieId == _movie2.Id);

            removedMovieBlocklists.Should().HaveCount(0);
            nonRemovedMovieBlocklists.Should().HaveCount(1);
        }

        [Test]
        public void in_memory_like_should_match_the_same_rows_as_the_title_and_hash_queries()
        {
            if (Db.DatabaseType == DatabaseType.PostgreSQL)
            {
                Assert.Ignore("In memory matching is used with SQLite only");
            }

            var titles = new[]
            {
                "Movie.Title.1998.1080p.BluRay-GRP", "movie_title_1998", "Movie%Title 50%", "ÄRGER.im.Paradies.2020", "Straße.2021",
                "Film \U0001F3AC Night", "abc", "ABC.[Release] (x)", "Movie.Title.1998"
            };

            var blocklist = titles.Select((t, i) => new Blocklist
                {
                    MovieId = 7,
                    Quality = new QualityModel(),
                    Languages = new List<Language>(),
                    SourceTitle = t,
                    TorrentInfoHash = i % 3 == 0 ? null : (i % 2 == 0 ? "ABCDEF" : "abc_def") + i,
                    Date = DateTime.UtcNow
                })
                .ToList();

            Db.InsertMany(blocklist);

            var rows = Subject.All().Where(b => b.MovieId == 7).ToList();

            var patterns = new[]
            {
                "movie.title.1998", "MOVIE.TITLE", "movie_title", "Movie%Title", "_ovie", "%", "_", "", "ärger", "ÄRGER", "STRASSE", "straße",
                "\U0001F3AC", "m_\U0001F3AC", "Film _ Night", "[release]", "(X)", "x", "Title.1998.1080p", "abcdef", "C_D", "ef2", "abc%2"
            };

            foreach (var pattern in patterns)
            {
                Subject.BlocklistedByTitle(7, pattern).Select(b => b.Id).Should()
                    .BeEquivalentTo(rows.Where(b => SqliteLike.Contains(b.SourceTitle, pattern)).Select(b => b.Id), "title pattern {0}", pattern);

                Subject.BlocklistedByTorrentInfoHash(7, pattern).Select(b => b.Id).Should()
                    .BeEquivalentTo(rows.Where(b => SqliteLike.Contains(b.TorrentInfoHash, pattern)).Select(b => b.Id), "hash pattern {0}", pattern);
            }
        }
    }
}
