# Ranks — agent entry point

A SwiftlyS2 ranking plugin for CS2: a **core** plugin plus four optional **modules**
that extend it through a shared-interface contract.

GitHub Copilot in VS Code loads `.github/copilot-instructions.md` automatically. If
your tool does not, **read that file first** — it holds the invariants that are not
obvious from the code, and breaking one of them is how this repo has broken before.

## Layout

| Path | What it is |
|------|------------|
| `Ranks/` | The core plugin. Owns the database, the ladder, points, menus and commands |
| `Ranks.Contract/` | The public contract modules compile against. Shipped from core's `resources/exports/` |
| `Modules/Ranks_*/` | Optional modules: `WeaponStats`, `EloPoints`, `CustomCombos`, `Prestige` |
| `.github/copilot-instructions.md` | Always-loaded rules: lifecycle, threading, auditing, the invariants table |
| `.github/instructions/*.instructions.md` | Topic rules, loaded only when you open a matching file |
| `docs/` | The specification (`FR-*` requirements) and the architecture rationale. Reference, not context |
| `Ranks.wiki/` | Operator-facing documentation. **A separate git repo** — commit it separately |

## Build

```powershell
dotnet build Ranks.Contract/Ranks.Contract.csproj -c Release
dotnet build Ranks/Ranks.csproj -c Release
dotnet build Modules/Ranks_WeaponStats/Ranks_WeaponStats.csproj -c Release
dotnet build Modules/Ranks_EloPoints/Ranks_EloPoints.csproj -c Release
dotnet build Modules/Ranks_CustomCombos/Ranks_CustomCombos.csproj -c Release
dotnet build Modules/Ranks_Prestige/Ranks_Prestige.csproj -c Release
```

Build the contract first — the others resolve it from its output. `dotnet publish`
additionally produces the distributable zip in each project's `build/`.

Treat warnings as failures: the tree is at zero and a new one is a regression.

## Verify

| Check | Command | Must report |
|-------|---------|-------------|
| Wiki links and key counts | `pwsh -File docs/verify-wiki.ps1` | `All wiki checks passed` |
| Release workflow logic | `docker run --rm -v "${PWD}:/src:ro" debian:bookworm-slim bash -c "apt-get update -qq && apt-get install -y -qq git python3 python3-yaml && cp -r /src /work && cd /work && git init -q && git add -A && git -c user.email=t@t -c user.name=t commit -qm base && bash docs/verify-release-workflow.sh"` | `failed: 0` |

Translations must stay key-complete: every key present in `resources/translations/`
and every lookup in code must agree. Keys resolved dynamically — `command.<name>.help`
from `commands.jsonc`, and `combo.<key>` in CustomCombos — look unused to a naive
scan and are **not** dead.

## The one rule to not break

A module may name a `Ranks.Contract` type **only** inside its `src/Api/RanksBridge.cs`,
and every member that does must carry `[MethodImpl(MethodImplOptions.NoInlining)]`.
The contract ships with core, so on a server without it the CLR fails to JIT any
method that mentions one — the exception lands in the *caller's* frame. Naming a
contract type anywhere else takes the whole server down rather than leaving the
module idle.

The corollary: anything reachable from a game event, a `Registrator` registration or
a scheduler callback must check `_bridge.IsAvailable` before touching the bridge.
`IsAvailable` is a plain `bool` and resolves nothing.
