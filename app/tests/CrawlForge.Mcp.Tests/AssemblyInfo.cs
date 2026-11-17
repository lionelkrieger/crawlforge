using Xunit;

// Socket-bound tests (loopback servers, interop) must not run in parallel:
// Windows HTTP.sys permits multiple HttpListener registrations on the same
// prefix and load-balances between them, which would cross-wire tokens
// between concurrently started test servers.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
