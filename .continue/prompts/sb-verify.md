---
name: sb-verify
description: Verify SecondBrain invariants before commit
invokable: true
---
Инструкция для самопроверки перед коммитом: проверь diff на отсутствие правок в `src/SecondBrain.Core/**`, отсутствие `DateTime.Now`/`DateTimeOffset.UtcNow`, отсутствие `File.WriteAllText` вне `AtomicWriter`, отсутствие `TODO`/`NotImplementedException`, и выведи статус гейта `dotnet build -warnaserror && dotnet test`.
