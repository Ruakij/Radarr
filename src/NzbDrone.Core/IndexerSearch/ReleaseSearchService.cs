using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
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
        Task<InteractiveSearchResult> InteractiveMovieSearch(int movieId, bool refresh, bool searchRemaining);
        InteractiveSearchStatus GetInteractiveSearchStatus(int movieId);
    }

    public class ReleaseSearchService : ISearchForReleases
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IMakeDownloadDecision _makeDownloadDecision;
        private readonly IMovieService _movieService;
        private readonly IMovieTranslationService _movieTranslationService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IConfigService _configService;
        private readonly ICached<IndexerQueryResult> _queryCache;
        private readonly ICached<InteractiveSearchEntry> _interactiveSearches;
        private readonly IUpgradableSpecification _upgradableSpecification;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly IndexerResponseTimeHistory _responseTimes = new IndexerResponseTimeHistory();
        private readonly Logger _logger;

        public ReleaseSearchService(IIndexerFactory indexerFactory,
                                IMakeDownloadDecision makeDownloadDecision,
                                IMovieService movieService,
                                IMovieTranslationService movieTranslationService,
                                IQualityProfileService qualityProfileService,
                                IConfigService configService,
                                ICacheManager cacheManager,
                                IUpgradableSpecification upgradableSpecification,
                                ICustomFormatCalculationService formatService,
                                Logger logger)
        {
            _indexerFactory = indexerFactory;
            _makeDownloadDecision = makeDownloadDecision;
            _movieService = movieService;
            _movieTranslationService = movieTranslationService;
            _qualityProfileService = qualityProfileService;
            _configService = configService;
            _queryCache = cacheManager.GetCache<IndexerQueryResult>(GetType(), "indexerQueries");
            _interactiveSearches = cacheManager.GetCache<InteractiveSearchEntry>(GetType(), "interactiveSearches");
            _upgradableSpecification = upgradableSpecification;
            _formatService = formatService;
            _logger = logger;
        }

        public async Task<List<DownloadDecision>> MovieSearch(int movieId, bool userInvokedSearch, bool interactiveSearch)
        {
            return await MovieSearch(GetMovieWithTranslations(movieId), userInvokedSearch, interactiveSearch);
        }

        // Cached queries answer at once, only the others are sent
        public async Task<List<DownloadDecision>> MovieSearch(Movie movie, bool userInvokedSearch, bool interactiveSearch)
        {
            var searchSpec = Get<MovieSearchCriteria>(movie, userInvokedSearch, interactiveSearch);

            var result = await Dispatch(searchSpec, GetIndexerGroups(GetIndexers(searchSpec)), true);

            return DeDupeDecisions(result.Decisions);
        }

        public async Task<InteractiveSearchResult> InteractiveMovieSearch(int movieId, bool refresh, bool searchRemaining)
        {
            var previous = searchRemaining ? _interactiveSearches.Find(movieId.ToString()) : null;
            var searchSpec = Get<MovieSearchCriteria>(GetMovieWithTranslations(movieId), true, true);
            var indexers = GetIndexers(searchSpec);

            List<ReleaseInfo> releases;
            List<DownloadDecision> decisions;
            List<IndexerSearchStatus> statuses;

            if (previous != null)
            {
                var doneIds = previous.Status.Indexers.Where(s => s.Status is IndexerSearchStatusType.Searched or IndexerSearchStatusType.Cached).Select(s => s.IndexerId).ToHashSet();
                var keptIds = indexers.Select(i => i.Definition.Id).Where(doneIds.Contains).ToHashSet();
                var keptReleases = previous.Releases.Where(r => keptIds.Contains(r.IndexerId)).ToList();

                // The remaining indexers are asked for explicitly, so they are searched at once instead of in priority groups
                var result = await Dispatch(searchSpec, new List<List<IIndexer>> { indexers.Where(i => !keptIds.Contains(i.Definition.Id)).ToList() }, !refresh);

                releases = keptReleases.Concat(result.Reports).ToList();
                decisions = _makeDownloadDecision.GetSearchDecision(keptReleases, searchSpec).Concat(result.Decisions).ToList();
                statuses = previous.Status.Indexers.Where(s => keptIds.Contains(s.IndexerId)).Concat(result.Statuses).ToList();
            }
            else
            {
                var result = await Dispatch(searchSpec, GetIndexerGroups(indexers), !refresh);

                releases = result.Reports;
                decisions = result.Decisions;
                statuses = result.Statuses;
            }

            var cachedAt = statuses.Min(s => s.CachedAt);
            var status = new InteractiveSearchStatus(cachedAt, statuses.OrderBy(s => s.Priority).ThenBy(s => s.Name).ToList());

            // Kept as long as the controller keeps the releases for grabbing
            _interactiveSearches.ClearExpired();
            _interactiveSearches.Set(movieId.ToString(), new InteractiveSearchEntry(releases, status), TimeSpan.FromMinutes(30));

            return new InteractiveSearchResult(DeDupeDecisions(decisions), status);
        }

        public InteractiveSearchStatus GetInteractiveSearchStatus(int movieId)
        {
            var status = _interactiveSearches.Find(movieId.ToString())?.Status;

            return status == null ? null : status with { Indexers = status.Indexers.Select(s => s with { History = _responseTimes.Get(s.IndexerId) }).ToList() };
        }

        // The key of an indexer query: the indexer and the requests it sends. A null key leaves the query uncached,
        // an indexer without a query sends no request for the movie
        private QueryKey GetQueryKey(IIndexer indexer, MovieSearchCriteria criteriaBase)
        {
            try
            {
                var key = indexer.GetSearchQueryKey(criteriaBase);

                return key == null ? new QueryKey(false, null) : new QueryKey(true, $"{indexer.Definition.Id}:{key}");
            }
            catch (Exception ex)
            {
                // Building the requests can fail like sending them, the query is then sent and reports the failure
                _logger.Debug(ex, "Unable to build the query of {0} for {1}", indexer.Definition.Name, criteriaBase);
                return new QueryKey(true, null);
            }
        }

        private IndexerQueryResult FindCachedQuery(string key)
        {
            var query = key == null ? null : _queryCache.Find(key);

            // A shortened lifetime also applies to the queries cached before
            return query != null && query.FetchedAt.AddMinutes(_configService.SearchResultCacheLifetime) > DateTime.UtcNow ? query : null;
        }

        private void StoreQuery(string key, IList<ReleaseInfo> releases)
        {
            var lifetime = _configService.SearchResultCacheLifetime;

            if (lifetime <= 0)
            {
                _queryCache.Clear();
                return;
            }

            if (key == null)
            {
                return;
            }

            // Cached<T> only evicts expired entries on lookup, so drop them here to keep the cache bounded
            _queryCache.ClearExpired();
            _queryCache.Set(key, new IndexerQueryResult(releases.ToList(), DateTime.UtcNow), TimeSpan.FromMinutes(lifetime));
        }

        private Movie GetMovieWithTranslations(int movieId)
        {
            var movie = _movieService.GetMovie(movieId);
            movie.MovieMetadata.Value.Translations = _movieTranslationService.GetAllTranslationsForMovieMetadata(movie.MovieMetadataId);

            return movie;
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

        private List<IIndexer> GetIndexers(SearchCriteriaBase criteriaBase)
        {
            var indexers = criteriaBase.InteractiveSearch ?
                _indexerFactory.InteractiveSearchEnabled() :
                _indexerFactory.AutomaticSearchEnabled();

            // Filter indexers to untagged indexers and indexers with intersecting tags
            return indexers.Where(i => i.Definition.Tags.Empty() || i.Definition.Tags.Intersect(criteriaBase.Movie.Tags).Any()).ToList();
        }

        // Groups are searched one after another, a single group unless indexers are searched in priority order
        private List<List<IIndexer>> GetIndexerGroups(List<IIndexer> indexers)
        {
            if (!_configService.EarlySearchReturn || !_configService.SearchIndexersInPriorityOrder)
            {
                return new List<List<IIndexer>> { indexers };
            }

            // Lower priority numbers are preferred, indexers up to the required priority are always waited for and so form the first group
            var requiredPriority = _configService.EarlySearchReturnRequiredPriority;

            return indexers.GroupBy(i => Math.Max(((IndexerDefinition)i.Definition).Priority, requiredPriority))
                .OrderBy(g => g.Key)
                .Select(g => g.ToList())
                .ToList();
        }

        private async Task<DispatchResult> Dispatch(MovieSearchCriteria criteriaBase, List<List<IIndexer>> groups, bool useCache)
        {
            var queryKeys = groups.SelectMany(g => g).ToDictionary(i => i.Definition.Id, i => GetQueryKey(i, criteriaBase));

            // Indexers that send no request for the movie are left out, they neither search nor show up in the status
            groups = groups.Select(g => g.Where(i => queryKeys[i.Definition.Id].HasQuery).ToList()).Where(g => g.Any()).ToList();

            var keys = groups.SelectMany(g => g).ToDictionary(i => i.Definition.Id, i => queryKeys[i.Definition.Id].Key);

            var cached = !useCache
                ? new Dictionary<int, IndexerQueryResult>()
                : keys.Select(k => (Id: k.Key, Query: FindCachedQuery(k.Value))).Where(q => q.Query != null).ToDictionary(q => q.Id, q => q.Query);

            var toSearchCount = keys.Count - cached.Count;

            if (cached.Any())
            {
                _logger.ProgressInfo("Using cached results of {0} indexers for {1}", cached.Count, criteriaBase);
            }

            if (toSearchCount > 0)
            {
                _logger.ProgressInfo("Searching indexers for {0}. {1} active indexers", criteriaBase, toSearchCount);
            }

            var decisions = new List<DownloadDecision>();
            var reports = new List<ReleaseInfo>();
            var answeredIndexerIds = new HashSet<int>();
            var searchedGroups = 0;
            var sentQueries = false;

            // Minimum Wait counts from the start of the search, not of each priority group
            var stopwatch = Stopwatch.StartNew();

            // A cached query answers at once, so a search with cached queries goes through the priority groups like one without, it only sends less
            for (var i = 0; i < groups.Count; i++)
            {
                if (i > 0 && decisions.Any(d => IsGoodEnough(d, criteriaBase)))
                {
                    _logger.ProgressInfo("Found a good enough release for {0}, skipping {1} indexers with lower priority", criteriaBase, groups.Skip(i).Sum(g => g.Count(indexer => !cached.ContainsKey(indexer.Definition.Id))));
                    break;
                }

                // No release is approved when the existing file meets the cutoff, lower priorities could not find an upgrade either
                if (i > 0 && MovieMeetsCutoff(criteriaBase))
                {
                    _logger.ProgressInfo("Existing file of {0} meets the cutoff, skipping {1} indexers with lower priority", criteriaBase, groups.Skip(i).Sum(g => g.Count(indexer => !cached.ContainsKey(indexer.Definition.Id))));
                    break;
                }

                var group = groups[i];

                searchedGroups++;

                decisions.AddRange(TakeCachedQueries(group, cached, criteriaBase, reports, answeredIndexerIds));

                var groupToSearch = group.Where(indexer => !cached.ContainsKey(indexer.Definition.Id)).ToList();

                if (groupToSearch.Empty())
                {
                    continue;
                }

                var tasks = groupToSearch.Select(indexer => DispatchIndexer(indexer, criteriaBase, keys[indexer.Definition.Id])).ToList();

                sentQueries = true;

                if (_configService.EarlySearchReturn && !criteriaBase.InteractiveSearch)
                {
                    var foundGoodRelease = decisions.Any(d => IsGoodEnough(d, criteriaBase));

                    decisions.AddRange(await CollectDecisionsWithEarlyReturn(groupToSearch, tasks, criteriaBase, reports, answeredIndexerIds, stopwatch, foundGoodRelease));
                }
                else
                {
                    var groupReports = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

                    reports.AddRange(groupReports);
                    answeredIndexerIds.UnionWith(groupToSearch.Select(indexer => indexer.Definition.Id));

                    _logger.ProgressDebug("Total of {0} reports were found for {1} from {2} indexers", groupReports.Count, criteriaBase, groupToSearch.Count);

                    decisions.AddRange(_makeDownloadDecision.GetSearchDecision(groupReports, criteriaBase));
                }
            }

            // Cached queries cost nothing, so those of groups the search did not reach are taken as well
            decisions.AddRange(TakeCachedQueries(groups.Skip(searchedGroups).SelectMany(g => g).ToList(), cached, criteriaBase, reports, answeredIndexerIds));

            // Update the last search time for movie if at least 1 query was sent.
            if (sentQueries)
            {
                var lastSearchTime = DateTime.UtcNow;
                _logger.Debug("Setting last search time to: {0}", lastSearchTime);

                criteriaBase.Movie.LastSearchTime = lastSearchTime;
                _movieService.UpdateLastSearchTime(criteriaBase.Movie);
            }

            var statuses = groups.SelectMany((group, g) => group.Select(indexer =>
            {
                var id = indexer.Definition.Id;

                if (cached.TryGetValue(id, out var hit))
                {
                    return GetStatus(indexer, IndexerSearchStatusType.Cached, reports) with { CachedAt = hit.FetchedAt };
                }

                if (g >= searchedGroups)
                {
                    return GetStatus(indexer, IndexerSearchStatusType.Skipped, reports);
                }

                if (!answeredIndexerIds.Contains(id))
                {
                    return GetStatus(indexer, IndexerSearchStatusType.NotWaitedFor, reports);
                }

                var durations = GetRequestDurations(criteriaBase, id);
                var status = GetStatus(indexer, IndexerSearchStatusType.Searched, reports) with
                {
                    QueryCount = durations.Count > 0 ? durations.Count : null,
                    MedianResponseMs = durations.Count > 0 ? IndexerResponseTimeHistory.Median(durations) : null
                };

                if (criteriaBase.IndexerFailures.TryGetValue(id, out var failure))
                {
                    var timedOut = failure is TaskCanceledException or TimeoutException or WebException { Status: WebExceptionStatus.Timeout };

                    return status with { Status = timedOut ? IndexerSearchStatusType.TimedOut : IndexerSearchStatusType.Failed, Message = failure.Message };
                }

                return status;
            })).ToList();

            return new DispatchResult(decisions, reports, statuses);
        }

        private List<DownloadDecision> TakeCachedQueries(List<IIndexer> indexers, Dictionary<int, IndexerQueryResult> cached, MovieSearchCriteria criteriaBase, List<ReleaseInfo> reports, HashSet<int> answeredIndexerIds)
        {
            var cachedIndexers = indexers.Where(i => cached.ContainsKey(i.Definition.Id)).ToList();
            var cachedReports = cachedIndexers.SelectMany(i => cached[i.Definition.Id].Releases).ToList();

            reports.AddRange(cachedReports);
            answeredIndexerIds.UnionWith(cachedIndexers.Select(i => i.Definition.Id));

            return cachedReports.Any() ? _makeDownloadDecision.GetSearchDecision(cachedReports, criteriaBase) : new List<DownloadDecision>();
        }

        private static List<double> GetRequestDurations(SearchCriteriaBase criteriaBase, int indexerId)
        {
            return criteriaBase.IndexerRequestDurations.TryGetValue(indexerId, out var durations) ? durations.Select(d => d.TotalMilliseconds).ToList() : new List<double>();
        }

        private static IndexerSearchStatus GetStatus(IIndexer indexer, IndexerSearchStatusType status, List<ReleaseInfo> reports, string message = null)
        {
            var id = indexer.Definition.Id;

            return new IndexerSearchStatus(id, indexer.Definition.Name, ((IndexerDefinition)indexer.Definition).Priority, status, reports.Count(r => r.IndexerId == id), message);
        }

        private async Task<List<DownloadDecision>> CollectDecisionsWithEarlyReturn(List<IIndexer> indexers, List<Task<IList<ReleaseInfo>>> tasks, SearchCriteriaBase criteriaBase, List<ReleaseInfo> allReports, HashSet<int> answeredIndexerIds, Stopwatch stopwatch, bool foundGoodRelease)
        {
            var minimumWait = TimeSpan.FromSeconds(_configService.EarlySearchReturnMinimumWait);
            var requiredPriority = _configService.EarlySearchReturnRequiredPriority;

            // Lower priority numbers are preferred
            var required = tasks.Where((task, i) => requiredPriority > 0 && ((IndexerDefinition)indexers[i].Definition).Priority <= requiredPriority).ToHashSet<Task>();

            var decisions = new List<DownloadDecision>();
            var pending = new List<Task>(tasks);

            using var delayCancellation = new CancellationTokenSource();

            try
            {
                while (pending.Any())
                {
                    Task completed;

                    if (!foundGoodRelease || pending.Any(required.Contains))
                    {
                        completed = await Task.WhenAny(pending);
                    }
                    else
                    {
                        var remaining = minimumWait - stopwatch.Elapsed;

                        // Past the minimum wait, indexers that already answered are still read, only those still running are dropped
                        completed = remaining > TimeSpan.Zero
                            ? await Task.WhenAny(pending.Append(Task.Delay(remaining, delayCancellation.Token)))
                            : pending.FirstOrDefault(t => t.IsCompleted);
                    }

                    if (completed == null)
                    {
                        break;
                    }

                    if (!pending.Remove(completed))
                    {
                        continue;
                    }

                    // Decisions are made per release, so deciding on each indexer's results as they arrive matches deciding on all of them at once
                    var completedTask = (Task<IList<ReleaseInfo>>)completed;
                    var reports = (await completedTask).ToList();
                    var batchDecisions = _makeDownloadDecision.GetSearchDecision(reports, criteriaBase, false);

                    allReports.AddRange(reports);
                    answeredIndexerIds.Add(indexers[tasks.IndexOf(completedTask)].Definition.Id);

                    decisions.AddRange(batchDecisions);
                    foundGoodRelease = foundGoodRelease || batchDecisions.Any(d => IsGoodEnough(d, criteriaBase));
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

            if (allReports.Count > 0)
            {
                _logger.ProgressInfo("Processed {0} releases for {1} from {2} indexers", allReports.Count, criteriaBase, tasks.Count - pending.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            return decisions;
        }

        // Good enough means the movie would not be upgraded from this release once grabbed
        private bool IsGoodEnough(DownloadDecision decision, SearchCriteriaBase criteriaBase)
        {
            return decision.Approved &&
                   !_upgradableSpecification.CutoffNotMet(criteriaBase.Movie.QualityProfile,
                       decision.RemoteMovie.ParsedMovieInfo.Quality,
                       decision.RemoteMovie.CustomFormats);
        }

        private bool MovieMeetsCutoff(SearchCriteriaBase criteriaBase)
        {
            var movie = criteriaBase.Movie;
            var file = movie.HasFile ? movie.MovieFile : null;

            return file != null && !_upgradableSpecification.CutoffNotMet(movie.QualityProfile, file.Quality, _formatService.ParseCustomFormat(file, movie));
        }

        // A query Early Search Return stopped waiting for still finishes here and fills the cache for later searches
        private async Task<IList<ReleaseInfo>> DispatchIndexer(IIndexer indexer, MovieSearchCriteria criteriaBase, string key)
        {
            var id = indexer.Definition.Id;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var releases = await indexer.Fetch(criteriaBase);

                // Indexers report most failures instead of throwing them, a failed answer is never cached
                if (!criteriaBase.IndexerFailures.ContainsKey(id))
                {
                    StoreQuery(key, releases);
                }

                return releases;
            }
            catch (Exception ex)
            {
                criteriaBase.IndexerFailures.TryAdd(id, ex);
                _logger.Error(ex, "Error while searching for {0}", criteriaBase);
            }
            finally
            {
                // Indexers that send no HTTP requests of their own count as one request per query
                if (!criteriaBase.IndexerRequestDurations.ContainsKey(id))
                {
                    criteriaBase.AddRequestDuration(id, stopwatch.Elapsed);
                }

                // Indexers report most failures instead of throwing them, queries not waited for still finish and count for the history
                if (!criteriaBase.IndexerFailures.ContainsKey(id))
                {
                    GetRequestDurations(criteriaBase, id).ForEach(d => _responseTimes.Add(id, d));
                }
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

        private record QueryKey(bool HasQuery, string Key);

        private record DispatchResult(List<DownloadDecision> Decisions, List<ReleaseInfo> Reports, List<IndexerSearchStatus> Statuses);
    }
}
