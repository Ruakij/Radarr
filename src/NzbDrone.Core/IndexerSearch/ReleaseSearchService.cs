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
        CachedSearchResult CachedMovieSearch(int movieId, bool userInvokedSearch, bool interactiveSearch);
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
        private readonly ICached<SearchResultCacheEntry> _searchResultCache;
        private readonly ICached<InteractiveSearchEntry> _interactiveSearches;
        private readonly IUpgradableSpecification _upgradableSpecification;
        private readonly Logger _logger;

        public ReleaseSearchService(IIndexerFactory indexerFactory,
                                IMakeDownloadDecision makeDownloadDecision,
                                IMovieService movieService,
                                IMovieTranslationService movieTranslationService,
                                IQualityProfileService qualityProfileService,
                                IConfigService configService,
                                ICacheManager cacheManager,
                                IUpgradableSpecification upgradableSpecification,
                                Logger logger)
        {
            _indexerFactory = indexerFactory;
            _makeDownloadDecision = makeDownloadDecision;
            _movieService = movieService;
            _movieTranslationService = movieTranslationService;
            _qualityProfileService = qualityProfileService;
            _configService = configService;
            _searchResultCache = cacheManager.GetCache<SearchResultCacheEntry>(GetType(), "searchResults");
            _interactiveSearches = cacheManager.GetCache<InteractiveSearchEntry>(GetType(), "interactiveSearches");
            _upgradableSpecification = upgradableSpecification;
            _logger = logger;
        }

        public async Task<List<DownloadDecision>> MovieSearch(int movieId, bool userInvokedSearch, bool interactiveSearch)
        {
            return await MovieSearch(GetMovieWithTranslations(movieId), userInvokedSearch, interactiveSearch);
        }

        public async Task<List<DownloadDecision>> MovieSearch(Movie movie, bool userInvokedSearch, bool interactiveSearch)
        {
            var searchSpec = Get<MovieSearchCriteria>(movie, userInvokedSearch, interactiveSearch);

            var result = await Dispatch(searchSpec, GetIndexerGroups(GetIndexers(searchSpec)));

            CacheSearchResults(movie, result.Reports, result.Statuses, DateTime.UtcNow);

            return DeDupeDecisions(result.Decisions);
        }

        public CachedSearchResult CachedMovieSearch(int movieId, bool userInvokedSearch, bool interactiveSearch)
        {
            var cached = FindCachedSearch(movieId, userInvokedSearch, interactiveSearch);

            return cached == null ? null : new CachedSearchResult(DeDupeDecisions(cached.Decisions), cached.Entry.SearchedAt);
        }

        public async Task<InteractiveSearchResult> InteractiveMovieSearch(int movieId, bool refresh, bool searchRemaining)
        {
            var previous = searchRemaining ? _interactiveSearches.Find(movieId.ToString()) : null;
            var cached = refresh || previous != null ? null : FindCachedSearch(movieId, true, true);
            var searchSpec = cached?.SearchSpec ?? Get<MovieSearchCriteria>(GetMovieWithTranslations(movieId), true, true);
            var indexers = cached?.Indexers ?? GetIndexers(searchSpec);

            List<ReleaseInfo> releases;
            List<DownloadDecision> decisions;
            List<IndexerSearchStatus> statuses;
            DateTime? cachedAt = null;

            if (cached != null)
            {
                releases = cached.Releases;
                decisions = cached.Decisions;
                cachedAt = cached.Entry.SearchedAt;

                // Indexers missing from a cached search that covers the interactive search were skipped by searching in priority order
                statuses = indexers.Select(i => GetStatus(i, cached.Entry.IndexerIds.Contains(i.Definition.Id) ? IndexerSearchStatusType.Cached : IndexerSearchStatusType.Skipped, releases)).ToList();
            }
            else if (previous != null)
            {
                var doneIds = SearchedIndexerIds(previous.Status.Indexers);
                var keptIds = indexers.Select(i => i.Definition.Id).Where(doneIds.Contains).ToHashSet();
                var keptReleases = previous.Releases.Where(r => keptIds.Contains(r.IndexerId)).ToList();

                // The remaining indexers are asked for explicitly, so they are searched at once instead of in priority groups
                var result = await Dispatch(searchSpec, new List<List<IIndexer>> { indexers.Where(i => !keptIds.Contains(i.Definition.Id)).ToList() });

                releases = keptReleases.Concat(result.Reports).ToList();
                decisions = _makeDownloadDecision.GetSearchDecision(keptReleases, searchSpec).Concat(result.Decisions).ToList();
                statuses = previous.Status.Indexers.Where(s => keptIds.Contains(s.IndexerId)).Concat(result.Statuses).ToList();
                cachedAt = previous.Status.CachedAt;

                CacheSearchResults(searchSpec.Movie, releases, statuses, cachedAt ?? DateTime.UtcNow);
            }
            else
            {
                var result = await Dispatch(searchSpec, GetIndexerGroups(indexers));

                releases = result.Reports;
                decisions = result.Decisions;
                statuses = result.Statuses;

                CacheSearchResults(searchSpec.Movie, releases, statuses, DateTime.UtcNow);
            }

            var status = new InteractiveSearchStatus(cachedAt, statuses.OrderBy(s => s.Priority).ThenBy(s => s.Name).ToList());

            // Kept as long as the controller keeps the releases for grabbing
            _interactiveSearches.ClearExpired();
            _interactiveSearches.Set(movieId.ToString(), new InteractiveSearchEntry(releases, status), TimeSpan.FromMinutes(30));

            return new InteractiveSearchResult(DeDupeDecisions(decisions), status);
        }

        public InteractiveSearchStatus GetInteractiveSearchStatus(int movieId)
        {
            return _interactiveSearches.Find(movieId.ToString())?.Status;
        }

        private CachedSearch FindCachedSearch(int movieId, bool userInvokedSearch, bool interactiveSearch)
        {
            var entry = _configService.SearchResultCacheLifetime > 0 ? _searchResultCache.Find(movieId.ToString()) : null;

            if (entry == null || entry.IndexerIds.Count == 0)
            {
                return null;
            }

            var movie = GetMovieWithTranslations(movieId);
            var searchSpec = Get<MovieSearchCriteria>(movie, userInvokedSearch, interactiveSearch);
            var indexers = GetIndexers(searchSpec);
            var indexerIds = indexers.Select(i => i.Definition.Id).ToHashSet();
            var releases = entry.Releases.Where(r => indexerIds.Contains(r.IndexerId)).ToList();

            // Decisions are made again so changes to the movie, profile, blocklist and queue since the search apply
            var decisions = _makeDownloadDecision.GetSearchDecision(releases, searchSpec);

            // Interactive search shows everything its indexers return, so it is only served by a search that got an answer from all indexers it would have searched
            if (interactiveSearch && !CoversSearchedIndexers(indexers, entry.IndexerIds, decisions, searchSpec))
            {
                _logger.Debug("Cached search results for {0} are missing results of some indexers, searching indexers", searchSpec);
                return null;
            }

            _logger.ProgressInfo("Using {0} search results for {1} cached at {2}", releases.Count, searchSpec, entry.SearchedAt.ToLocalTime());

            return new CachedSearch(searchSpec, indexers, entry, releases, decisions);
        }

        private bool CoversSearchedIndexers(List<IIndexer> indexers, HashSet<int> answeredIndexerIds, List<DownloadDecision> decisions, SearchCriteriaBase criteriaBase)
        {
            var groups = GetIndexerGroups(indexers);

            for (var i = 0; i < groups.Count; i++)
            {
                var groupIds = groups[i].Select(indexer => indexer.Definition.Id).ToHashSet();

                if (!groupIds.IsSubsetOf(answeredIndexerIds))
                {
                    return false;
                }

                if (i < groups.Count - 1 && decisions.Any(d => groupIds.Contains(d.RemoteMovie.Release.IndexerId) && IsGoodEnough(d, criteriaBase)))
                {
                    return true;
                }
            }

            return true;
        }

        private static HashSet<int> SearchedIndexerIds(List<IndexerSearchStatus> statuses)
        {
            return statuses.Where(s => s.Status is IndexerSearchStatusType.Searched or IndexerSearchStatusType.Cached).Select(s => s.IndexerId).ToHashSet();
        }

        private void CacheSearchResults(Movie movie, List<ReleaseInfo> reports, List<IndexerSearchStatus> statuses, DateTime searchedAt)
        {
            var lifetime = _configService.SearchResultCacheLifetime;

            if (lifetime <= 0)
            {
                _searchResultCache.Clear();
                return;
            }

            // Cached<T> only evicts expired entries on lookup, so drop them here to keep the cache bounded
            _searchResultCache.ClearExpired();

            var indexerIds = SearchedIndexerIds(statuses);

            // A search no indexer answered has nothing to serve, caching it would keep automatic searches from asking the indexers again
            if (indexerIds.Count == 0)
            {
                return;
            }

            _searchResultCache.Set(movie.Id.ToString(), new SearchResultCacheEntry(reports, indexerIds, searchedAt), TimeSpan.FromMinutes(lifetime));
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

        private async Task<DispatchResult> Dispatch(MovieSearchCriteria criteriaBase, List<List<IIndexer>> groups)
        {
            var indexerCount = groups.Sum(g => g.Count);

            _logger.ProgressInfo("Searching indexers for {0}. {1} active indexers", criteriaBase, indexerCount);

            var decisions = new List<DownloadDecision>();
            var reports = new List<ReleaseInfo>();
            var answeredIndexerIds = new HashSet<int>();
            var searchedGroups = 0;

            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var tasks = group.Select(indexer => DispatchIndexer(indexer, criteriaBase)).ToList();

                searchedGroups++;
                List<DownloadDecision> groupDecisions;

                if (_configService.EarlySearchReturn && !criteriaBase.InteractiveSearch)
                {
                    groupDecisions = await CollectDecisionsWithEarlyReturn(group, tasks, criteriaBase, reports, answeredIndexerIds);
                }
                else
                {
                    var groupReports = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

                    reports.AddRange(groupReports);
                    answeredIndexerIds.UnionWith(group.Select(indexer => indexer.Definition.Id));

                    _logger.ProgressDebug("Total of {0} reports were found for {1} from {2} indexers", groupReports.Count, criteriaBase, group.Count);

                    groupDecisions = _makeDownloadDecision.GetSearchDecision(groupReports, criteriaBase);
                }

                decisions.AddRange(groupDecisions);

                if (i < groups.Count - 1 && groupDecisions.Any(d => IsGoodEnough(d, criteriaBase)))
                {
                    _logger.ProgressInfo("Found a good enough release for {0}, skipping {1} indexers with lower priority", criteriaBase, groups.Skip(i + 1).Sum(g => g.Count));
                    break;
                }
            }

            // Update the last search time for movie if at least 1 indexer was searched.
            if (indexerCount > 0)
            {
                var lastSearchTime = DateTime.UtcNow;
                _logger.Debug("Setting last search time to: {0}", lastSearchTime);

                criteriaBase.Movie.LastSearchTime = lastSearchTime;
                _movieService.UpdateLastSearchTime(criteriaBase.Movie);
            }

            var statuses = groups.SelectMany((group, g) => group.Select(indexer =>
            {
                var id = indexer.Definition.Id;

                if (g >= searchedGroups)
                {
                    return GetStatus(indexer, IndexerSearchStatusType.Skipped, reports);
                }

                if (!answeredIndexerIds.Contains(id))
                {
                    return GetStatus(indexer, IndexerSearchStatusType.NotWaitedFor, reports);
                }

                if (criteriaBase.IndexerFailures.TryGetValue(id, out var failure))
                {
                    var timedOut = failure is TaskCanceledException or TimeoutException or WebException { Status: WebExceptionStatus.Timeout };

                    return GetStatus(indexer, timedOut ? IndexerSearchStatusType.TimedOut : IndexerSearchStatusType.Failed, reports, failure.Message);
                }

                return GetStatus(indexer, IndexerSearchStatusType.Searched, reports);
            })).ToList();

            return new DispatchResult(decisions, reports, statuses);
        }

        private static IndexerSearchStatus GetStatus(IIndexer indexer, IndexerSearchStatusType status, List<ReleaseInfo> reports, string message = null)
        {
            var id = indexer.Definition.Id;

            return new IndexerSearchStatus(id, indexer.Definition.Name, ((IndexerDefinition)indexer.Definition).Priority, status, reports.Count(r => r.IndexerId == id), message);
        }

        private async Task<List<DownloadDecision>> CollectDecisionsWithEarlyReturn(List<IIndexer> indexers, List<Task<IList<ReleaseInfo>>> tasks, SearchCriteriaBase criteriaBase, List<ReleaseInfo> allReports, HashSet<int> answeredIndexerIds)
        {
            var minimumWait = TimeSpan.FromSeconds(_configService.EarlySearchReturnMinimumWait);
            var requiredPriority = _configService.EarlySearchReturnRequiredPriority;

            // Lower priority numbers are preferred
            var required = tasks.Where((task, i) => requiredPriority > 0 && ((IndexerDefinition)indexers[i].Definition).Priority <= requiredPriority).ToHashSet<Task>();

            var decisions = new List<DownloadDecision>();
            var pending = new List<Task>(tasks);
            var foundGoodRelease = false;
            var stopwatch = Stopwatch.StartNew();

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

        private async Task<IList<ReleaseInfo>> DispatchIndexer(IIndexer indexer, MovieSearchCriteria criteriaBase)
        {
            try
            {
                return await indexer.Fetch(criteriaBase);
            }
            catch (Exception ex)
            {
                criteriaBase.IndexerFailures.TryAdd(indexer.Definition.Id, ex);
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

        private record DispatchResult(List<DownloadDecision> Decisions, List<ReleaseInfo> Reports, List<IndexerSearchStatus> Statuses);

        private record CachedSearch(MovieSearchCriteria SearchSpec, List<IIndexer> Indexers, SearchResultCacheEntry Entry, List<ReleaseInfo> Releases, List<DownloadDecision> Decisions);
    }
}
