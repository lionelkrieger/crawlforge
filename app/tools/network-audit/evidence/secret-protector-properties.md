# Criterion #17 — protector properties

- Scheme: cfsec1: (AES-256-GCM, authenticated; prefix as AAD)
- Round-trip: pass
- Tampered ciphertext: CryptographicException (rejected)
- Cross-key decrypt: CryptographicException (rejected)
- Key material: 32 bytes from RandomNumberGenerator per store, persisted locally,
  independently random — no hardware/OS-identity derivation anywhere in EntropyKeyStore