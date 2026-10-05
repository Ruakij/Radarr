using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.IndexerSearch
{
    public interface ISearchForReleases
    {
        Task<List<DownloadDecision>> MovieSearch(int movieId, bool userInvokedSearch, bool interactiveSearch);
        Task<List<DownloadDecision>> MovieSearch(Movie movie, bool userInvokedSearch, bool interactiveSearch);
    }

    public class ReleaseSearchService : ISearchForReleases
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IMakeDownloadDecision _makeDownloadDecision;
        private readonly IMovieService _movieService;
        private readonly IMovieTranslationService _movieTranslationService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public ReleaseSearchService(IIndexerFactory indexerFactory,
                                IMakeDownloadDecision makeDownloadDecision,
                                IMovieService movieService,
                                IMovieTranslationService movieTranslationService,
                                IQualityProfileService qualityProfileService,
                                IConfigService configService,
                                Logger logger)
        {
            _indexerFactory = indexerFactory;
            _makeDownloadDecision = makeDownloadDecision;
            _movieService = movieService;
            _movieTranslationService = movieTranslationService;
            _qualityProfileService = qualityProfileService;
            _configService = configService;
            _logger = logger;
        }

        public async Task<List<DownloadDecision>> MovieSearch(int movieId, bool userInvokedSearch, bool interactiveSearch)
        {
            var movie = _movieService.GetMovie(movieId);
            movie.MovieMetadata.Value.Translations = _movieTranslationService.GetAllTranslationsForMovieMetadata(movie.MovieMetadataId);

            return await MovieSearch(movie, userInvokedSearch, interactiveSearch);
        }

        public async Task<List<DownloadDecision>> MovieSearch(Movie movie, bool userInvokedSearch, bool interactiveSearch)
        {
            var downloadDecisions = new List<DownloadDecision>();

            var searchSpec = Get<MovieSearchCriteria>(movie, userInvokedSearch, interactiveSearch);

            var decisions = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
            downloadDecisions.AddRange(decisions);

            return DeDupeDecisions(downloadDecisions);
        }

        private TSpec Get<TSpec>(Movie movie, bool userInvokedSearch, bool interactiveSearch)
            where TSpec : SearchCriteriaBase, new()
        {
            var spec = new TSpec
            {
                Movie = movie,
                UserInvokedSearch = userInvokedSearch,
                InteractiveSearch = interactiveSearch
            };

            var wantedLanguages = _qualityProfileService.GetAcceptableLanguages(movie.QualityProfileId);
            var translations = _movieTranslationService.GetAllTranslationsForMovieMetadata(movie.MovieMetadataId);

            var queryTranslations = new List<string>
            {
                movie.MovieMetadata.Value.Title,
                movie.MovieMetadata.Value.OriginalTitle
            };

            // Add Translation of wanted languages to search query
            foreach (var translation in translations.Where(a => wantedLanguages.Contains(a.Language)))
            {
                queryTranslations.Add(translation.Title);
            }

            spec.SceneTitles = queryTranslations.Where(t => t.IsNotNullOrWhiteSpace()).Distinct(StringComparer.InvariantCultureIgnoreCase).ToList();

            return spec;
        }

        private async Task<List<DownloadDecision>> Dispatch(Func<IIndexer, Task<IList<ReleaseInfo>>> searchAction, SearchCriteriaBase criteriaBase)
        {
            var indexers = criteriaBase.InteractiveSearch ?
                _indexerFactory.InteractiveSearchEnabled() :
                _indexerFactory.AutomaticSearchEnabled();

            // Filter indexers to untagged indexers and indexers with intersecting tags
            indexers = indexers.Where(i => i.Definition.Tags.Empty() || i.Definition.Tags.Intersect(criteriaBase.Movie.Tags).Any()).ToList();

            _logger.ProgressInfo("Searching indexers for {0}. {1} active indexers", criteriaBase, indexers.Count);

            var tasks = indexers.Select(indexer => DispatchIndexer(searchAction, indexer, criteriaBase)).ToList();

            List<DownloadDecision> decisions;

            if (_configService.EarlySearchReturn && !criteriaBase.InteractiveSearch)
            {
                decisions = await CollectDecisionsWithEarlyReturn(tasks, criteriaBase);
            }
            else
            {
                var batch = await Task.WhenAll(tasks);

                var reports = batch.SelectMany(x => x).ToList();

                _logger.ProgressDebug("Total of {0} reports were found for {1} from {2} indexers", reports.Count, criteriaBase, indexers.Count);

                decisions = _makeDownloadDecision.GetSearchDecision(reports, criteriaBase);
            }

            // Update the last search time for movie if at least 1 indexer was searched.
            if (indexers.Any())
            {
                var lastSearchTime = DateTime.UtcNow;
                _logger.Debug("Setting last search time to: {0}", lastSearchTime);

                criteriaBase.Movie.LastSearchTime = lastSearchTime;
                _movieService.UpdateLastSearchTime(criteriaBase.Movie);
            }

            return decisions;
        }

        private async Task<List<DownloadDecision>> CollectDecisionsWithEarlyReturn(List<Task<IList<ReleaseInfo>>> tasks, SearchCriteriaBase criteriaBase)
        {
            var minimumWait = TimeSpan.FromSeconds(_configService.EarlySearchReturnMinimumWait);
            var timeout = TimeSpan.FromSeconds(_configService.EarlySearchReturnTimeout);
            var scoreThreshold = _configService.EarlySearchReturnCustomFormatScore;

            var decisions = new List<DownloadDecision>();
            var pending = new List<Task>(tasks);
            var reportCount = 0;
            var foundGoodRelease = false;
            var stopwatch = Stopwatch.StartNew();

            using var delayCancellation = new CancellationTokenSource();

            try
            {
                while (pending.Any())
                {
                    var remaining = (foundGoodRelease ? minimumWait : timeout) - stopwatch.Elapsed;

                    // Past the deadline, indexers that already answered are still read, only those still running are dropped
                    var completed = remaining > TimeSpan.Zero
                        ? await Task.WhenAny(pending.Append(Task.Delay(remaining, delayCancellation.Token)))
                        : pending.FirstOrDefault(t => t.IsCompleted);

                    if (completed == null)
                    {
                        break;
                    }

                    if (!pending.Remove(completed))
                    {
                        continue;
                    }

                    // Decisions are made per release, so deciding on each indexer's results as they arrive matches deciding on all of them at once
                    var reports = (await (Task<IList<ReleaseInfo>>)completed).ToList();
                    var batchDecisions = _makeDownloadDecision.GetSearchDecision(reports, criteriaBase, false);

                    reportCount += reports.Count;

                    decisions.AddRange(batchDecisions);
                    foundGoodRelease = foundGoodRelease || batchDecisions.Any(d => d.Approved && d.RemoteMovie.CustomFormatScore >= scoreThreshold);
                }
            }
            finally
            {
                delayCancellation.Cancel();
            }

            if (pending.Any())
            {
                _logger.ProgressInfo("Returning early for {0} after {1:0.#}s, ignoring results of {2} pending indexers", criteriaBase, stopwatch.Elapsed.TotalSeconds, pending.Count);
            }

            if (reportCount > 0)
            {
                _logger.ProgressInfo("Processed {0} releases for {1} from {2} indexers", reportCount, criteriaBase, tasks.Count - pending.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            return decisions;
        }

        private async Task<IList<ReleaseInfo>> DispatchIndexer(Func<IIndexer, Task<IList<ReleaseInfo>>> searchAction, IIndexer indexer, SearchCriteriaBase criteriaBase)
        {
            try
            {
                return await searchAction(indexer);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error while searching for {0}", criteriaBase);
            }

            return Array.Empty<ReleaseInfo>();
        }

        private List<DownloadDecision> DeDupeDecisions(List<DownloadDecision> decisions)
        {
            // De-dupe reports by guid so duplicate results aren't returned. Pick the one with the least rejections and higher indexer priority.
            return decisions.GroupBy(d => d.RemoteMovie.Release.Guid)
                .Select(d => d.OrderBy(v => v.Rejections.Count()).ThenBy(v => v.RemoteMovie?.Release?.IndexerPriority ?? IndexerDefinition.DefaultPriority).First())
                .ToList();
        }
    }
}
