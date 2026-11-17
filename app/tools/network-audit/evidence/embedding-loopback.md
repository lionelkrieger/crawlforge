# Exclusion E7 — embedding endpoint loopback enforcement

- Rejected: private LAN (192.168/10/172.16), public hosts, vendor APIs, non-http schemes.
- Accepted: 127.0.0.1, ::1, localhost only.
- localhost resolves to the loopback IP WITHOUT DNS (hosts-file poisoning cannot
  redirect daemon traffic off-machine).
- Validation is re-applied before every daemon request (defense in depth,
  LoopbackDaemonEmbeddingProvider).