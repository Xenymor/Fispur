# Fispur — Project Memory

Chess engine written in C# by **Xenymor**. Built on top of Sebastian Lague's
"Chess Coding Challenge" framework (the `API` + `Framework` code is his;
Fispur adds the engine logic and the UCI CLI). This file is Claude's
project memory — read it first to orient before diving into files.

## What this is

- A UCI-compatible chess engine with an NNUE evaluation.
- Ships two things: a **Raylib GUI** (Lague's challenge app, for watching bots
  play) and a **UCI command-line wrapper** so the engine can run in any UCI
  GUI / tester (cutechess, OpenBench, etc.).
- Old versions are not kept as source snapshots in the tree — they live in git
  history; regression testing and SPSA tuning run through OpenBench.

## Solution layout (`Fispur.sln`)

Three projects, all `net10.0`:

| Project | Role |
|---|---|
| `Fispur-Engine/Fispur-Engine.csproj` | Class library: the API, the chess framework, the engine and the embedded NNUE net. No UI dependencies. All engine code lives here. |
| `Fispur/Fispur-gui.csproj` | The Raylib GUI. References `Fispur-Engine`; depends on `Raylib-cs 4.5` and `Microsoft.CodeAnalysis` (token counter). |
| `Fispur-cli-(exp)/Fispur-cli-(exp).csproj` | UCI wrapper around the engine (`Program.cs`) plus the OpenBench bench (`Bench.cs`). |

The CLI only `ProjectReference`s `Fispur-Engine`, so the GUI is not part of its
build. The root namespace of `Fispur-Engine` and the GUI is **`FispurEngine`**
(not `Fispur`) — `Fispur` is the engine class, so the namespace has to differ.

## Where the code lives

`Fispur-Engine/src/`:

- **`API/`** — the restricted bot-facing API (namespace `FispurEngine.API`):
  `Board`, `Move`, `Timer`, `Piece`, `Square`, `PieceList`, `PieceType`,
  `BitboardHelper`, and the `IChessBot` interface. The engine is written against
  *this*, not the full framework.
- **`Framework/Chess/`** — the underlying chess implementation (namespace
  `FispurEngine.Chess`): `Board`, `MoveGenerator` (magic bitboards), `Zobrist`,
  `RepetitionTable`, FEN/PGN helpers, `Arbiter`/result logic.
- **`Framework/Application/Helpers/API Helpers/`** — move generation / move
  parsing helpers the API calls into (namespace `FispurEngine.Application.APIHelpers`).
- **`Fispur/`** — the **engine**, class `Fispur` (namespace `FispurEngine`),
  split into partial files:
  - `Fispur.cs` — constants (`MATE`, `INFINITY`, `MAX_DEPTH` = 256, hash
    defaults 256 MB / max 1024 MB), `GetName` / `GetAuthor`, `SetHashSize`,
    `NewGame`, `Stop` / `PrepareSearch`.
  - `Fispur.Think.cs` — `Think(board, timer, maxDepth)`: iterative deepening,
    aspiration windows, time management, UCI `info` lines with PV.
  - `Fispur.Search.cs` — `alphaBeta`, `qSearch`, `shouldPrune`, make/undo
    wrappers, LMR table.
  - `Fispur.TT.cs` — transposition table.
  - `Fispur.History.cs` — butterfly, continuation, capture and correction history.
  - `Fispur.MoveOrdering.cs` — move scoring, killers, selection sort `pickMove`.
  - `Fispur.SEE.cs` — static exchange evaluation.
  - `Fispur.Tune.cs` — `Tunables` (SPSA parameters) and the `Tuning` registry.
  - `NNUE.cs` — the network: loading, accumulators, evaluation.

`Fispur/src/`:

- **`Framework/Application/`** — the Raylib GUI + match runner (namespace
  `FispurEngine.Application`): `Core/Program.cs` (GUI entry point),
  `Core/ChallengeController.cs`, `Core/Settings.cs`, players, UI, `Token Counter`.

## Engine techniques

**Search** (`Fispur.Search.cs`, `Fispur.Think.cs`)
- PVS negamax with iterative deepening; aspiration windows from depth 5
  (widened by doubling, full window past `ASPWindowReset`).
- Check extension (limited to ply ≤ 2·rootDepth), internal iterative reduction
  without a TT hit.
- Reverse futility pruning (with improving), null-move pruning (skipped in
  pawn-only endgames), singular extensions with double extensions (non-PV),
  negative extensions and multicut.
- Move-loop pruning: futility pruning, late move pruning, SEE pruning for
  captures and quiets with history-scaled thresholds.
- LMR from a log·log table, reduced by quiet history, increased when not improving.
- Quiescence search with TT cutoffs, stand pat, check evasions, losing captures skipped.
- Draws: fifty-move rule, repetition, insufficient material.

**Transposition table** (`Fispur.TT.cs`) — one entry per slot, 32-bit key check,
stores score, move, depth, bound and the raw static eval; depth-preferred
replacement for the same key; prefetch after `MakeMove`; mate scores ply-adjusted.

**Move ordering** (`Fispur.MoveOrdering.cs`) — TT move > SEE-good captures
(MVV + capture history) > killer > quiet history (butterfly + 1-ply
continuation history) > SEE-bad captures.

**Evaluation** — NNUE, corrected by a pawn-hash correction history
(`Fispur.History.cs`).

**Time management** (`Fispur.Think.cs`) — soft/hard limits derived from
`remaining / TmBaseDiv + increment * TmIncPct`; the soft limit is scaled by the
best move's node fraction and best-move stability. `MoveOverhead` is subtracted
from every budget. `go movetime` and `go depth` / `infinite` are handled separately.

**Tunables** (`Fispur.Tune.cs`) — every `[Tunable(default, min, max)]` field in
`Tunables` is exposed as a UCI spin option; `poisoned = true` marks
integer-step parameters that get small SPSA steps. Values are frozen at the
first search (`Tuning.Freeze`), later `setoption`s for them are ignored. The CLI
command `spsa` prints the OpenBench SPSA input lines.

## NNUE (`NNUE.cs`)

- Architecture: (768 → 1024)×2 → 1, 10 king buckets with horizontal mirroring
  (king on files e–h flips the board), 8 output buckets by piece count, SCReLU,
  QA 255 / QB 64 / scale 400. Requires AVX2.
- Accumulator stack per ply with incremental updates; king-bucket changes refresh
  through Finny tables (cached accumulators per bucket and mirror state).
- The net is embedded as manifest resource **`net.bin`**. Default source:
  `Fispur-Engine/Training/Nets/10kb-1024hl-8ob.bin`. Building with
  `-p:EVALFILE=<path>` (or `make EVALFILE=<path>`) embeds that file instead;
  relative paths resolve against the directory the build was started in, a
  missing file fails the build. The loader checks the file size against the
  architecture constants, so a net of a different shape needs matching constant
  changes in `NNUE.cs`.

## UCI CLI (`Fispur-cli-(exp)`)

- `Program.cs` implements `uci`, `isready`, `ucinewgame`,
  `position [startpos|fen ...] [moves ...]`, `go` (`wtime`/`btime`/`winc`/`binc`/
  `movetime`/`depth`/`infinite`), `stop`, `setoption` (`Hash`, `MoveOverhead`,
  `Threads` fixed at 1, all tunables), `bench [depth]`, `spsa`, `quit`.
- The search runs on its own thread with a 32 MB stack; `bestmove` is formatted
  from `Move.ToString()` via a substring.
- `<exe> bench [depth]` as a command-line argument runs the bench and exits.
- `Bench.cs`: depth 12, fixed 16 MB hash, fixed position list, engine reset per
  position. The node count is the OpenBench fingerprint — changing the depth, the
  positions, the search or the net changes it.

## Build / run

- Requires .NET SDK 10. VS 2022 17.14 builds it with warning NETSDK1233;
  VS 2026 / Rider / `dotnet` CLI are fine.
- GUI: run the `Fispur-gui` project (Raylib window).
- Engine for a UCI GUI/tester: build/run `Fispur-cli-(exp)`.
- OpenBench: `make EXE=<name> [EVALFILE=<net>]` in the repo root publishes
  `Fispur-cli-(exp)` as a single self-contained `<name>[.exe]` — that is the exact
  contract the OpenBench worker expects. The `OB_EXE` property drives the rename
  and switches the GUI's asset copies off. The publish goes to `ob-publish/` and a
  Copy target in `Fispur-cli-(exp).csproj` puts the binary into the root — never
  publish with `-o .` (SDK 10.0.2xx+ excludes PublishDir/** from compilation,
  i.e. every project).
- OpenBench networks: upload a net on the server's Networks page and pick it as
  dev/base network of a test (or set it as Fispur's default); the worker passes
  it as `EVALFILE`. The test's bench must be the one the engine prints with that
  net (`make EXE=Fispur EVALFILE=<net>`, then `Fispur bench`).
- Worker setup: `Scripts/setup-worker.ps1` (Windows) and `Scripts/setup-worker.sh` (Linux).

## Supporting folders

- **`Openings/`** — `UHO_4060_v2.epd` (opening/test positions, ~15 MB).
- **`Fispur-Engine/Training/Nets/`** — the default NNUE net.
- **`Fispur/resources/`** — `Fens.txt`, `Pieces.png`, `Fonts/` (GUI assets).
- **`Scripts/`** — OpenBench worker setup scripts.
- `.github/copilot-instructions.md` currently only has Azure boilerplate (ignore).

## Conventions

- New engine work goes in `Fispur-Engine/src/Fispur/`. `GetName` returns `"dev"`
  during development; set it when cutting a release — no source snapshot is archived.
- Git: work on `main` or a feature branch; commits are short and version-oriented
  (e.g. "Implement basic transposition table").
