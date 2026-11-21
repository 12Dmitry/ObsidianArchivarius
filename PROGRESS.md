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
- [ ] P3 half