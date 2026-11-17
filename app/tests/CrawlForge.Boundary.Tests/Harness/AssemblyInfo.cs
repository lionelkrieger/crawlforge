using Xunit;

// Boundary evidence requires quiet, serialized phases: connection and
// assembly-load monitoring is process-wide, so tests must not overlap.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
