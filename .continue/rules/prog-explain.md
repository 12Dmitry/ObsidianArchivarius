---
description: A description of your rule
---

- Target: net10.0. Nullable enable. TreatWarningsAsErrors true. No new NuGet without asking.
- NEVER edit src/SecondBrain.Core/** — contracts are frozen. If a contract is wrong: STOP, explain, wait.
- No TODO / NotImplementedException. Ship complete code with full namespaces and usings.
- Time: inject TimeProvider. Never DateTime.Now / DateTimeOffset.UtcNow directly.
- Files: only via AtomicWriter inside FileSystemVaultStore. No File.WriteAllText elsewhere.
- LLM output: always validated against a JSON schema + C# semantic checks. LLM never produces id/path/dates/wikilinks.
- Every component ships with xUnit tests. Workflow tests must pass WITHOUT Ollama running (use cassettes/NSubstitute).
- After each task: run `dotnet build -warnaserror && dotnet test`, paste output, update PROGRESS.md, stop.

Ollama chat:    POST /api/chat  {model, messages, stream:false, format:<json_schema>, options:{temperature, top_p, top_k, presence_penalty, num_ctx}}
Ollama embed:   POST /api/embed {model:"bge-m3", input:[...], truncate:false, keep_alive:"10m", options:{num_gpu:0}}  → {embeddings:[[...]]}
bge-m3: 1024 dims, 8192 ctx, NO query/doc prefix, L2-normalize vectors, MIT [[58]]
num_gpu:0 forces CPU [[60]]; truncate:false turns silent truncation into an error
T-lite presets: extract → temp 0.15, top_p 0.8, top_k 20, presence_penalty 0.0
prose   → temp 0.7,  top_p 0.8, top_k 20, presence_penalty 1.0 [[1]]
FTS5:  CREATE VIRTUAL TABLE chunks_fts USING fts5(body_norm, chunk_id UNINDEXED, tokenize='trigram');
vec0:  CREATE VIRTUAL TABLE vec_chunks USING vec0(chunk_id TEXT PRIMARY KEY, embedding float[1024] distance_metric=cosine);
load:  conn.EnableExtensions(true); conn.LoadExtension("vec0", "sqlite3_vec_init");