using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Blocklisting
{
    public interface IBlocklistService
    {
        bool Blocklisted(int movieId, ReleaseInfo release);
        bool BlocklistedTorrentHash(int movieId, string hash);
        PagingSpec<Blocklist> Paged(PagingSpec<Blocklist> pagingSpec);
        List<Blocklist> GetByMovieId(int movieId);
        void Block(RemoteMovie remoteMovie, string message);
        void Delete(int id);
        void Delete(List<int> ids);
    }

    public class BlocklistService : IBlocklistService,

                                    IExecute<ClearBlocklistCommand>,
                                    IHandle<DownloadFailedEvent>,
                                    IHandleAsync<MoviesDeletedEvent>
    {
        private readonly IBlocklistRepository _blocklistRepository;
        private readonly IMainDatabase _database;

        public BlocklistService(IBlocklistRepository blocklistRepository, IMainDatabase database)
        {
            _blocklistRepository = blocklistRepository;
            _database = database;
        }

        public bool Blocklisted(int movieId, ReleaseInfo release)
        {
            if (release.DownloadProtocol == DownloadProtocol.Torrent)
            {
                if (release is not TorrentInfo torrentInfo)
                {
                    return false;
                }

                if (torrentInfo.InfoHash.IsNotNullOrWhiteSpace())
                {
                    return BlocklistedByTorrentInfoHash(movieId, torrentInfo.InfoHash).Any(b => SameTorrent(b, torrentInfo));
                }

                return BlocklistedByTitle(movieId, release.Title)
                    .Where(b => b.Protocol == DownloadProtocol.Torrent)
                    .Any(b => SameTorrent(b, torrentInfo));
            }

            return BlocklistedByTitle(movieId, release.Title)
                .Where(b => b.Protocol == DownloadProtocol.Usenet)
                .Any(b => SameNzb(b, release));
        }

        // A decision run checks every release against the movie blocklist, so SQLite loads it once per run and matches
        // like the LIKE query does. PostgreSQL keeps the query, as its ILIKE case folding depends on the server locale.
        private IEnumerable<Blocklist> BlocklistedByTitle(int movieId, string title)
        {
            if (_database.DatabaseType == DatabaseType.PostgreSQL)
            {
                return _blocklistRepository.BlocklistedByTitle(movieId, title);
            }

            return GetMovieBlocklist(movieId).Where(b => SqliteLike.Contains(b.SourceTitle, title));
        }

        private IEnumerable<Blocklist> BlocklistedByTorrentInfoHash(int movieId, string infoHash)
        {
            if (_database.DatabaseType == DatabaseType.PostgreSQL)
            {
                return _blocklistRepository.BlocklistedByTorrentInfoHash(movieId, infoHash);
            }

            return GetMovieBlocklist(movieId).Where(b => SqliteLike.Contains(b.TorrentInfoHash, infoHash));
        }

        private List<Blocklist> GetMovieBlocklist(int movieId)
        {
            return DecisionRunCache.Get($"blocklist:{movieId}", () => _blocklistRepository.BlocklistedByMovie(movieId));
        }

        public bool BlocklistedTorrentHash(int movieId, string hash)
        {
            return _blocklistRepository.BlocklistedByTorrentInfoHash(movieId, hash).Any(b =>
                b.TorrentInfoHash.Equals(hash, StringComparison.InvariantCultureIgnoreCase));
        }

        public PagingSpec<Blocklist> Paged(PagingSpec<Blocklist> pagingSpec)
        {
            return _blocklistRepository.GetPaged(pagingSpec);
        }

        public List<Blocklist> GetByMovieId(int movieId)
        {
            return _blocklistRepository.BlocklistedByMovie(movieId);
        }

        public void Block(RemoteMovie remoteMovie, string message)
        {
            var blocklist = new Blocklist
                            {
                                MovieId = remoteMovie.Movie.Id,
                                SourceTitle =  remoteMovie.Release.Title,
                                Quality = remoteMovie.ParsedMovieInfo.Quality,
                                Date = DateTime.UtcNow,
                                PublishedDate = remoteMovie.Release.PublishDate,
                                Size = remoteMovie.Release.Size,
                                Indexer = remoteMovie.Release.Indexer,
                                Protocol = remoteMovie.Release.DownloadProtocol,
                                Message = message,
                                Languages = remoteMovie.ParsedMovieInfo.Languages
                            };

            if (remoteMovie.Release is TorrentInfo torrentRelease)
            {
                blocklist.TorrentInfoHash = torrentRelease.InfoHash;
            }

            _blocklistRepository.Insert(blocklist);
        }

        public void Delete(int id)
        {
            _blocklistRepository.Delete(id);
        }

        public void Delete(List<int> ids)
        {
            _blocklistRepository.DeleteMany(ids);
        }

        private bool SameNzb(Blocklist item, ReleaseInfo release)
        {
            return ReleaseComparer.SameNzb(new ReleaseComparerModel(item), release);
        }

        private bool SameTorrent(Blocklist item, TorrentInfo release)
        {
            return ReleaseComparer.SameTorrent(new ReleaseComparerModel(item), release);
        }

        public void Execute(ClearBlocklistCommand message)
        {
            _blocklistRepository.Purge();
        }

        public void Handle(DownloadFailedEvent message)
        {
            var blocklist = new Blocklist
            {
                MovieId = message.MovieId,
                SourceTitle = message.SourceTitle,
                Quality = message.Quality,
                Date = DateTime.UtcNow,
                PublishedDate = DateTime.Parse(message.Data.GetValueOrDefault("publishedDate")),
                Size = long.Parse(message.Data.GetValueOrDefault("size", "0")),
                Indexer = message.Data.GetValueOrDefault("indexer"),
                Protocol = (DownloadProtocol)Convert.ToInt32(message.Data.GetValueOrDefault("protocol")),
                Message = message.Message,
                Languages = message.Languages,
                TorrentInfoHash = message.TrackedDownload?.Protocol == DownloadProtocol.Torrent
                    ? message.TrackedDownload.DownloadItem.DownloadId
                    : message.Data.GetValueOrDefault("torrentInfoHash", null)
            };

            if (Enum.TryParse(message.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
            {
                blocklist.IndexerFlags = flags;
            }

            _blocklistRepository.Insert(blocklist);
        }

        public void HandleAsync(MoviesDeletedEvent message)
        {
            _blocklistRepository.DeleteForMovies(message.Movies.Select(m => m.Id).ToList());
        }
    }
}
