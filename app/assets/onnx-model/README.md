# Bundled ONNX Embedding Model — all-MiniLM-L6-v2

| Asset control | Value |
|---|---|
| Workpiece | WP-04-02 (phase-04 wave-4A) |
| Model tag | `onnx:all-MiniLM-L6-v2:384` |
| Dimensions | 384 |
| License | Apache-2.0 |
| Acquisition | One-time, build-time (permitted); **zero runtime downloads, ever** |
| Integrity | `manifest.json` (SHA-256 per file) |

## Files

| File | Purpose | SHA-256 |
|---|---|---|
| `model.onnx` | Sentence-embedding transformer, ONNX export (float32, ~90 MB) | `759c3cd2b7fe7e93933ad23c4c9181b7396442a2ed746ec7c1d46192c469c46e` |
| `vocab.txt` | bert-base-uncased WordPiece vocabulary (30,522 tokens) | `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3` |
| `manifest.json` | Checksum manifest + model metadata | — |

## Provenance

- **Base model:** `sentence-transformers/all-MiniLM-L6-v2`
  (https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) — a 6-layer
  MiniLM sentence-transformer producing 384-dimensional sentence embeddings.
  License: **Apache-2.0** (model card). The MiniLM architecture originates from
  Microsoft Research (Wang et al., "MiniLM: Deep Self-Attention Distillation
  for Task-Agnostic Compression of Pre-Trained Transformers").
- **ONNX export:** `Xenova/all-MiniLM-L6-v2`
  (https://huggingface.co/Xenova/all-MiniLM-L6-v2, `onnx/model.onnx`) — the
  transformers.js ONNX conversion of the same weights, license Apache-2.0.
  Inputs: `input_ids`, `attention_mask`, `token_type_ids` (int64, batch ×
  sequence). Output: token states (batch × sequence × 384); the provider
  applies mask-aware mean pooling + L2 normalization, matching the
  sentence-transformers pooling convention.
- **Vocabulary:** `vocab.txt` from the base model repository
  (bert-base-uncased WordPiece; special ids `[PAD]=0`, `[UNK]=100`,
  `[CLS]=101`, `[SEP]=102`). The bundled `WordPieceTokenizer` is a
  self-contained implementation — no tokenizer package or service is used.

## Runtime boundary (exclusion E7, criterion #45)

- The runtime (`BundledOnnxEmbeddingProvider`) loads **only** these bundled
  files through the local ONNX runtime (Microsoft.ML.OnnxRuntime, CPU
  inference). The ONNX runtime opens no network.
- There is **no download path** in the product: missing assets raise a local
  configuration error and the semantic passes degrade to an
  `intelligence.embeddings-unavailable` diagnostic — audits never fail and
  nothing is fetched.
- The optional alternative provider is a **user-configured loopback-only
  daemon**; non-loopback endpoints are rejected at configuration and
  connections are pinned to the loopback IP without DNS. No remote embedding
  API exists anywhere in the module.
- Asset updates ship only with product updates (§13 packaging manifest).

## Verification

`manifest.json` checksums are asserted by the Embeddings test suite
(`OnnxProviderTests`), which also proves local inference, dimensionality,
determinism, and that similar sentences score above dissimilar ones.
