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
- [x] P4 DONE 

---
| Этап | Что уже собрано и проверено | Ключевая гарантия |
| :--- | :--- | :--- |
| **T0 + T1 (P1)** | `SecondBrain.slnx` (.NET 10), 5 проектов, CPM, `AGENTS.md`, правила Continue, весь замороженный `SecondBrain.Core` (Appendix A). | Строгая граница `Cli → Infrastructure → Core`, `TreatWarningsAsErrors`, контракты заморожены. |
| **P2 (Кодек)** | `Slugger` (RU→ASCII, ё/й до FormD, ≤60, коллизии) + `FrontmatterCodec` (YamlDotNet, UnknownKeys, порядок ключей, CRLF/LF). | Метаданные и неизвестные YAML-ключи переживают round-trip; `updated` сам не меняется. |
| **P3 (Хранилище и откат)** | `ManagedBlockCodec`, `VaultPathPolicy`, `AtomicWriter` (`File.Replace`/`File.Move`), `FileSystemVaultStore` (100 циклов CAS `ExpectedHash`), `JsonlWriteJournal` + `WritePlanExecutor`. | Ручные правки вне `<!-- brain:X:begin/end -->` неприкосновенны байт-в-байт; сбой в середине `WritePlan` автоматически откатывает заметку и обратную ссылку. |
---

Сценарий	Статус на T4c-1	Что уже умеет движок / что откроется после T4c-2
Заметки (capture) без LLM	Логика готова и протестирована	Парсинг >> folder: / >> type: / >> #tag / >> map: / >> alias: / >> url:, транслит-слаг ≤60, уникальный ID, dry-run diff по умолчанию, атомарная запись + мгновенный индекс FTS5 при --apply. После T4c-2 — запуск одной командой из терминала.
Дневник (diary) без LLM	Логика готова и протестирована	Автофайл 20_Areas/Diary/YYYY-MM-DD.md, append записи с якорем ^e-HHMM (и ^e-HHmm-2 при коллизии в ту же минуту), чанкинг by_entry, мгновенная индексация. После T4c-2 — быстрый ввод из консоли + --stats.
Русский полнотекстовый поиск (search)	Индекс готов и протестирован	ё→е + RuStemmer (основы ≥3 для trigram), защита от спецсимволов в MATCH, fallback для <3 символов, мягкие бусты тегов/типов/префиксов вместо жёсткого WHERE. После T4c-2 — ранжированная выдача в терминале со сниппетами.
Безопасная запись, UnknownKeys, ManagedBlocks, откат	Полностью готово (P2–P3)	Чужие YAML-поля не теряются, updated не трогается без повода, ручные правки вне <!-- brain:X:begin/end --> неприкосновенны, мульти-операции откатываются целиком (WritePlanExecutor + JsonlWriteJournal).
CLI (doctor / show / rebuild-index)	Следующий шаг (T4c-2)	Пока Program.cs — заглушка. Сделаем самописный диспетчер (~80 строк, без System.CommandLine).
LLM, bge-m3, Eval/абляция, Insight	Дальше (P5–P8)	Пока нет локальных моделей (Ollama), векторного поиска vec0/RRF, контура метрик §8 (LinkEvalRunner) и сценария инсайтов.