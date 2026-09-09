# Local embedding fixtures

The checked-in JSON fixtures contain no private repository text. `tokenizer-parity.json` is generated from the exact immutable model/vocabulary/tokenizer artifacts in `src/Threadsmith.Embeddings.Local/minilm-assets.json`, using Python `onnxruntime==1.22.1`, `tokenizers==0.22.2`, and `numpy==2.4.6`. It verifies empty input, accents, CJK/emoji, special tokens, whitespace, full 256-token input, and overflow. The reference explicitly disables tokenizer.json's default padding/truncation before accounting, then compares dynamic and 256-token padded inference. All 384 components are checked with maximum absolute tolerance 0.00005, allowing CPU architecture rounding differences.

To regenerate offline after explicit `eng/Stage-EmbeddingAssets.ps1` staging:

```powershell
python tests/Threadsmith.Embeddings.Local.Tests/Fixtures/generate-reference.py --assets artifacts/embedding-assets/embeddings/all-MiniLM-L12-v2 --output tests/Threadsmith.Embeddings.Local.Tests/Fixtures/tokenizer-parity.json
```

`retrieval-fixture.json` version 1 freezes eight repository notes and twenty requests, split into nine calibration and eleven held-out examples before choosing the threshold. Calibration minimizes twice false inclusions plus missed relevant memories; ties minimize misses then choose the larger threshold. Only calibration queries select `SemanticMinimum`. The measured value is 0.47. The held-out hybrid fixture admits one related but undesired privacy note for a public-documentation question and misses zero expected relevant notes; its explicit unrelated requests return no matches. This exposes the limitation of vector similarity for opposing or qualified preferences rather than claiming universal semantic accuracy.

Normal test discovery never accesses online services or downloads models. Set `THREADSMITH_EMBEDDING_INTEGRATION=1` to run the offline native parity/cancellation/retrieval tests. `THREADSMITH_EMBEDDING_REPORT` optionally writes raw measured timings and rankings to a caller-selected scratch JSON file. Timing checks report evidence without asserting a universal speed on unrelated hardware. The reference development-host warm-turn budget is 25 ms p95 for one uncached retrieval plus its inclusion receipt on this small fixture; it is not a 256-token worst-case SLA. New maximum-length or slower-host measurements must be reported separately.
