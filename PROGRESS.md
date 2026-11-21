# Progress

## Initial Setup
- [x] Create solution with projects:
  - `src/SecondBrain.Core`
  - `src/SecondBrain.Infrastructure`
  - `src/SecondBrain.Cli`
  - `tests/SecondBrain.Tests`
  - `tests/SecondBrain.Tests.Live`
- [x] Configure `Directory.Build.props` (net10.0, nullable, warnaserror, implicit usings)
- [x] Configure `Directory.Packages.props` (CPM)
- [x] Configure `global.json`
- [x] Add `NoteMetadata.cs` and related records to `src/SecondBrain.Core/Model/NoteMetadata.cs`
- [x] Pass Gate: `dotnet build SecondBrain.slnx -warnaserror; dotnet test SecondBrain.slnx --no-build --verbosity normal`

## Milestone T1
- [x] T0: Scaffold solution & core setup (`DONE`)
- [x] T1 (P1): Core contracts & initial setup verified (`DONE`)
- [x] P2: DONE add FrontmatterCodec with unknown keys, canonical order and golden tests
- [x] P3 DONE 

---
| Этап | Что уже собрано и проверено | Ключевая гарантия |
| :--- | :--- | :--- |
| **T0 + T1 (P1)** | `SecondBrain.slnx` (.NET 10), 5 проектов, CPM, `AGENTS.md`, правила Continue, весь замороженный `SecondBrain.Core` (Appendix A). | Строгая граница `Cli → Infrastructure → Core`, `TreatWarningsAsErrors`, контракты заморожены. |
| **P2 (Кодек)** | `Slugger` (RU→ASCII, ё/й до FormD, ≤60, коллизии) + `FrontmatterCodec` (YamlDotNet, UnknownKeys, порядок ключей, CRLF/LF). | Метаданные и неизвестные YAML-ключи переживают round-trip; `updated` сам не меняется. |
| **P3 (Хранилище и откат)** | `ManagedBlockCodec`, `VaultPathPolicy`, `AtomicWriter` (`File.Replace`/`File.Move`), `FileSystemVaultStore` (100 циклов CAS `ExpectedHash`), `JsonlWriteJournal` + `WritePlanExecutor`. | Ручные правки вне `<!-- brain:X:begin/end -->` неприкосновенны байт-в-байт; сбой в середине `WritePlan` автоматически откатывает заметку и обратную ссылку. |
---

