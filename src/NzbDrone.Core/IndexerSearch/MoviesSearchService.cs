using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.IndexerSearch
{
    public class MovieSearchService : IExecute<MoviesSearchCommand>, IExecute<MissingMoviesSearchCommand>, IExecute<CutoffUnmetMoviesSearchCommand>
    {
        private readonly IMovieService _movieService;
        private readonly IMovieCutoffService _movieCutoffService;
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IQueueService _queueService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public MovieSearchService(IMovieService movieService,
                                   IMovieCutoffService movieCutoffService,
                                   ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   IQueueService queueService,
                                   IConfigService configService,
                                   Logger logger)
        {
            _movieService = movieService;
            _movieCutoffService = movieCutoffService;
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _queueService = queueService;
            _configService = configService;
            _logger = logger;
        }

        public void Execute(MoviesSearchCommand message)
        {
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;

            var movies = _movieService.GetMovies(message.MovieIds)
                .Where(m => (m.Monitored && m.IsAvailable()) || userInvokedSearch)
                .ToList();

            // A search started by hand asks for fresh results, its results still refresh the cache
            SearchForBulkMovies(movies, userInvokedSearch, !userInvokedSearch, message.FallbackToIndexers).GetAwaiter().GetResult();
        }

        public void Execute(MissingMoviesSearchCommand message)
        {
            var pagingSpec = new PagingSpec<Movie>
            {
                Page = 1,
                PageSize = 100000,
                SortDirection = SortDirection.Ascending,
                SortKey = "Id"
            };

            pagingSpec.FilterExpressions.Add(v => v.Monitored == true);

            var movies = _movieService.MoviesWithoutFiles(pagingSpec).Records.ToList();

            var queue = _queueService.GetQueue().Where(q => q.Movie != null).Select(q => q.Movie.Id);
            var missing = movies.Where(e => !queue.Contains(e.Id)).ToList();

            SearchForBulkMovies(missing, message.Trigger == CommandTrigger.Manual, true).GetAwaiter().GetResult();
        }

        public void Execute(CutoffUnmetMoviesSearchCommand message)
        {
            var pagingSpec = new PagingSpec<Movie>
            {
                Page = 1,
                PageSize = 100000,
                SortDirection = SortDirection.Ascending,
                SortKey = "Id"
            };

            pagingSpec.FilterExpressions.Add(v => v.Monitored == true);

            var movies = _movieCutoffService.MoviesWhereCutoffUnmet(pagingSpec).Records.ToList();

            var queue = _queueService.GetQueue().Where(q => q.Movie != null).Select(q => q.Movie.Id);
            var missing = movies.Where(e => !queue.Contains(e.Id)).ToList();

            SearchForBulkMovies(missing, message.Trigger == CommandTrigger.Manual, true).GetAwaiter().GetResult();
        }

        private async Task SearchForBulkMovies(List<Movie> movies, bool userInvokedSearch, bool useCache, bool fallbackToIndexers = false)
        {
            _logger.ProgressInfo("Performing search for {0} movies", movies.Count);
            var movieIds = movies.GroupBy(e => e.Id).OrderBy(g => g.Min(m => m.LastSearchTime ?? DateTime.MinValue)).Select(g => g.Key).ToList();

            // Cached results are looked up ahead with the searches, a search runs ahead only for a movie without cached results
            var downloadedCount = await SearchAndProcess(movieIds,
                _configService.SearchConcurrency,
                async movieId =>
                {
                    var cached = useCache ? FindCachedSearch(movieId, userInvokedSearch) : null;

                    return (Cached: cached, Decisions: cached == null ? await SearchIndexers(movieId, userInvokedSearch) : null);
                },
                async (movieId, result) =>
                {
                    var grabbedCount = 0;
                    var decisions = result.Decisions;

                    if (result.Cached != null)
                    {
                        var cachedResult = await ProcessCachedSearch(movieId, result.Cached);

                        if (cachedResult != null)
                        {
                            grabbedCount += cachedResult.Grabbed.Count;

                            if (!fallbackToIndexers || cachedResult.Grabbed.Any() || cachedResult.Pending.Any())
                            {
                                return grabbedCount;
                            }

                            _logger.Debug("No cached search result for movie [{0}] is acceptable anymore, searching indexers", movieId);
                        }

                        decisions = await SearchIndexers(movieId, userInvokedSearch);
                    }

                    return decisions == null ? grabbedCount : grabbedCount + (await _processDownloadDecisions.ProcessDecisions(decisions)).Grabbed.Count;
                });

            _logger.ProgressInfo("Completed search for {0} movies. {1} reports downloaded.", movies.Count, downloadedCount);
        }

        private CachedSearchResult FindCachedSearch(int movieId, bool userInvokedSearch)
        {
            try
            {
                return _releaseSearchService.CachedMovieSearch(movieId, userInvokedSearch, false);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to use cached search results for movie: [{0}], searching indexers", movieId);
            }

            return null;
        }

        private async Task<ProcessedDecisions> ProcessCachedSearch(int movieId, CachedSearchResult cached)
        {
            try
            {
                return await _processDownloadDecisions.ProcessDecisions(cached.Decisions);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to use cached search results for movie: [{0}], searching indexers", movieId);
            }

            return null;
        }

        private async Task<List<DownloadDecision>> SearchIndexers(int movieId, bool userInvokedSearch)
        {
            try
            {
                return await _releaseSearchService.MovieSearch(movieId, userInvokedSearch, false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to search for movie: [{0}]", movieId);
                return null;
            }
        }

        // Runs up to `concurrency` searches at once and processes their results one after another in the given order,
        // so grabs happen in the same order as in a sequential search. Returns the sum of the processed counts.
        internal static async Task<int> SearchAndProcess<T, TResult>(IEnumerable<T> items, int concurrency, Func<T, Task<TResult>> search, Func<T, TResult, Task<int>> process)
        {
            var pending = items.ToList();
            var searches = new List<Task<TResult>>();
            var count = 0;

            for (var i = 0; i < pending.Count; i++)
            {
                while (searches.Count < pending.Count && searches.Count < i + Math.Max(1, concurrency))
                {
                    searches.Add(search(pending[searches.Count]));
                }

                count += await process(pending[i], await searches[i]);
            }

            return count;
        }
    }
}
