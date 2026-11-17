namespace CrawlForge.Data.Sqlite.Tests.Conformance;

/// <summary>
/// Provider conformance case catalog (precedence rule 6: provider agnosticism).
/// These are the LOGICAL cases every CrawlForge database provider must satisfy with
/// identical behavior (FS-CRAWL-001 §2.4, §9.1, §9.3, §9.4). The MySQL provider
/// implements the same case set independently in its own test project; parity is
/// verified at the orchestrator gate. Case ids are stable across providers.
/// </summary>
internal static class ConformanceCaseCatalog
{
    /// <summary>All logical conformance cases, in stable order.</summary>
    public static IReadOnlyList<(string Id, string Title)> Cases { get; } =
    [
        ("CONF-01", "Provider identity: stable Kind and dialect recorded in the migration ledger"),
        ("CONF-02", "Connection open: configured database reachable, basic query succeeds, file created"),
        ("CONF-03", "Durability: write connections use write-ahead logging"),
        ("CONF-04", "Durability: synchronous level matches the configured WAL-safe profile"),
        ("CONF-05", "Contention: busy timeout matches the configured value"),
        ("CONF-06", "Integrity: foreign keys are enforced on every connection"),
        ("CONF-07", "Atomicity: multi-row writes are transactional (rollback and commit)"),
        ("CONF-08", "CQRS: read connections reject writes and DDL"),
        ("CONF-09", "CQRS: single-writer discipline serializes concurrent write leases"),
        ("CONF-10", "CQRS: concurrent readers succeed while a write lease is held"),
        ("CONF-11", "Capability: regular-expression predicates in queries"),
        ("CONF-12", "Capability: in-database delimited-text import"),
        ("CONF-13", "Capability: in-database delimited-text export with quoting"),
        ("CONF-14", "Capability: vector-similarity search over stored embeddings"),
        ("CONF-15", "Capability: extended mathematical and statistical functions"),
        ("CONF-16", "Capability probe reports all four required families available"),
        ("CONF-17", "Migration compatibility: fresh database without ledger is compatible"),
        ("CONF-18", "Migration compatibility: ledger carrying this dialect is compatible"),
        ("CONF-19", "Migration compatibility: ledger carrying a foreign dialect is incompatible"),
        ("CONF-20", "Connection test verdict: success flags and unreachable failure flags"),
        ("CONF-21", "Convention: surrogate integer keys auto-assign"),
        ("CONF-22", "Convention: UTC timestamps round-trip without drift"),
        ("CONF-23", "Convention: JSON text payload columns round-trip and are queryable"),
        ("CONF-24", "Convention: soft delete with transactional pruning"),
        ("CONF-25", "Index enforcement: unique (audit, canonical URL) rejects duplicates"),
        ("CONF-26", "Observability: WAL size hook reports bytes, passive and local"),
        ("CONF-27", "Offline: first-run initialization uses only local file I/O"),
        ("CONF-28", "Baseline defaults: per-user data location and WAL-safe default profile"),
    ];
}
