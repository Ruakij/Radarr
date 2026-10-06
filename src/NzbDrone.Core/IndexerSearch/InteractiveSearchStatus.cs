using System;
using System.Collections.Generic;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.IndexerSearch
{
    // The answer of one indexer to one query, all pages included
    public record IndexerQueryResult(List<ReleaseInfo> Releases, DateTime FetchedAt);

    public enum IndexerSearchStatusType
    {
        Cached,
        Searched,
        Skipped,
        NotWaitedFor,
        Failed,
        TimedOut
    }

    // CachedAt is the fetch time of cached results. QueryCount and MedianResponseMs cover the HTTP requests of this search, pages and failed requests included,
    // an indexer without HTTP requests of its own counts one per query. History covers the requests of the last successful queries of all searches
    public record IndexerSearchStatus(int IndexerId, string Name, int Priority, IndexerSearchStatusType Status, int ReleaseCount, string Message, DateTime? CachedAt = null, int? QueryCount = null, double? MedianResponseMs = null, IndexerResponseTimes History = null);

    // Low and High bound the middle 95% of the response times
    public record IndexerResponseTimes(int Count, double MedianMs, double LowMs, double HighMs);

    // CachedAt is the time of the oldest cached results the search used, null when every indexer was searched
    public record InteractiveSearchStatus(DateTime? CachedAt, List<IndexerSearchStatus> Indexers);

    public record InteractiveSearchResult(List<DownloadDecision> Decisions, InteractiveSearchStatus Status);

    // The releases are kept so the remaining indexers can be searched later and merged in
    public record InteractiveSearchEntry(List<ReleaseInfo> Releases, InteractiveSearchStatus Status);
}
