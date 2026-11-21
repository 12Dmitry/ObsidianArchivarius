---
name: SecondBrain Guardrails
alwaysApply: true
---
- Target `net10.0`. `Nullable` enable. `TreatWarningsAsErrors` true. No new NuGet without asking.
- NEVER edit `src/SecondBrain.Core/**` — contracts are frozen. If a contract is wrong: STOP, explain, wait.
- No `TODO` / `NotImplementedException`. Complete code, full namespaces and usings.
- Inject `TimeProvider`. Never call `DateTime.Now` / `DateTimeOffset.UtcNow` directly.
- Files only via `AtomicWriter` inside `FileSystemVaultStore`. No `File.WriteAllText` elsewhere.
- LLM is extractor/planner only: never emit `id`, `path`, `created/updated`, `[[wikilinks]]`. Closed-world linking (`out_of_candidate_links = 0`). Always validate LLM output against JSON schema + C# semantic checks.
- Forbidden on MVP: MCP, `Microsoft.Extensions.AI`, Qdrant, Obsidian Local REST API, 50+ tool-call agent loops, `cron`, `nomic-embed-text`.
- Every component ships xUnit tests. Workflow tests pass WITHOUT Ollama (cassettes/`NSubstitute`).
- After each task: run `dotnet build -warnaserror && dotnet test`, paste output, update `PROGRESS.md`, stop.
