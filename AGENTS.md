# Repository contract

Authority order starts at [.work/README.md](.work/README.md). Read the relevant subsystem specification and accepted ADRs before changing that subsystem.

Architecture rules:

- FoxData.Core must not reference Application, Infrastructure, hosting, transport or source implementations.
- FoxData.Application must not reference Infrastructure or transport hosts.
- Source adapters must enter through the documented Ingestion/Evidence boundary; they must not write canonical or evidence tables directly.
- Do not introduce new infrastructure, stronger source guarantees or cross-module dependencies without evidence and an accepted ADR.
- Do not change adjacent subsystems unless the task requires it.

Required verification:

~~~bash
dotnet restore FoxData.slnx --locked-mode
dotnet format FoxData.slnx --verify-no-changes --no-restore
dotnet build FoxData.slnx -c Release --no-restore
dotnet test --solution FoxData.slnx -c Release --no-build
~~~

Work on a branch and merge through a PR. Do not commit implementation directly to main. If implementation changes a documented invariant, update the relevant .work specification in the same PR.
