using Xunit;

// Scale benchmarks are heavyweight and share the seeded data directory; run
// them sequentially so measurements never contend with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
