using System;
using System.Collections.Generic;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.IndexerSearch
{
    public record CachedSearchResult(List<DownloadDecision> Decisions, DateTime SearchedAt);

    // IndexerIds are the indexers that answered without failing, a search returned early lacks the ones still pending
    public record SearchResultCacheEntry(List<ReleaseInfo> Releases, HashSet<int> IndexerIds, DateTime SearchedAt);

    public enum IndexerSearchStatusType
    {
        Searched,
        Cached,
        Skipped,
        NotWaitedFor,
        Failed,
        TimedOut
    }

    public record IndexerSearchStatus(int IndexerId, string Name, int Priority, IndexerSearchStatusType Status, int ReleaseCount, string Message);

    // CachedAt is the time of the cached results the search used, null when every indexer was searched
    public record InteractiveSearchStatus(DateTime? CachedAt, List<IndexerSearchStatus> Indexers);

    public record InteractiveSearchResult(List<DownloadDecision> Decisions, InteractiveSearchStatus Status);

    // The releases are kept so the remaining indexers can be searched later and merged in
    public record InteractiveSearchEntry(List<ReleaseInfo> Releases, InteractiveSearchStatus Status);
}
