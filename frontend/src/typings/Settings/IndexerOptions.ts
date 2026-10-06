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
}
