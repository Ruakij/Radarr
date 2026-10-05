using System;
using System.Collections.Generic;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.IndexerSearch
{
    public record CachedSearchResult(List<DownloadDecision> Decisions, DateTime SearchTime);

    // IndexerIds are the indexers that answered, a search returned early lacks the ones still pending
    public record SearchResultCacheEntry(List<ReleaseInfo> Releases, HashSet<int> IndexerIds, DateTime SearchTime);
}
