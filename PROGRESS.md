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
- [x] Pass Gate: `dotnet build -warnaserror && dotnet test`
