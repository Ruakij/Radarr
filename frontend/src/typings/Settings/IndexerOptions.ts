export default interface IndexerOptions {
  minimumAge: number;
  retention: number;
  maximumSize: number;
  rssSyncInterval: number;
  preferIndexerFlags: boolean;
  availabilityDelay: number;
  searchConcurrency: number;
  whitelistedHardcodedSubs: string[];
  allowHardcodedSubs: boolean;
  earlySearchReturn: boolean;
  earlySearchReturnMinimumWait: number;
  earlySearchReturnRequiredPriority: number;
  searchIndexersInPriorityOrder: boolean;
  searchResultCacheLifetime: number;
}
