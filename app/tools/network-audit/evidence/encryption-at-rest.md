# Criterion #17 — five credential classes encrypted at rest

| Class | Surface | Storage |
|---|---|---|
| Site signing material | SignedRequestSecretStore | signing-material.json (cfsec1:) |
| Destination credentials | POST /api/destinations (ftp) | export_destinations.encrypted_credentials_json |
| Proxy credentials | PUT /api/settings | application_settings storage proxy.passwordProtected |
| External-DB credentials | PUT /api/settings (mysql) | application_settings storage database.passwordProtected |
| Local automation token | McpTokenService | mcp-token.json (cfsec1:) |

- Files scanned in data directory: 12 (transient handshake descriptors excluded — retired at shutdown, proven separately)
- Plaintext secret leaks: **0** (required: 0)
- Files carrying protected (cfsec1:) material: 3
- Settings read masks passwords: yes (********)
- Key derivation: app-generated 32-byte RandomNumberGenerator entropy (EntropyKeyStore), never hardware/OS identity

Leaks: none