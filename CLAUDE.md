# Fispur — Project Memory

Chess engine written in C# by **Xenymor**. Built on top of Sebastian Lague's
"Chess Coding Challenge" framework (the `API` + `Framework` code is his;
Fispur adds the engine logic and the UCI CLI). This file is Claude's
project memory — read it first to orient before diving into files.

## What this is

- A UCI-compatible chess engine (`GetName` / `GetAuthor` / `Think(board, timer)`).
- Ships two things: a **Raylib GUI** (Lague's challenge app, for watching bots
  play) and a **UCI command-line wrapper** so the engine can run in any UCI
  GUI / tester (cutechess, OpenBench, etc.).
- Old versions are not kept as source snapshots in the tree any more — they
  live in git history; regression testing runs through OpenBench.

## Solution layout (`Fispur.sln`, VS 2022)

Two projects:

| Project | TFM | Role |
|---|---|---|
| `Fispur` | net6.0 | The framework + all bot code + the Raylib GUI. Depends on `Raylib-cs 4.5` and `Microsoft.CodeAnalysis` (token counter). This is where all real code lives. |
| `Fispur-cli-(exp)` | net8.0 | UCI wrapper that runs the **current/WIP** engine — class `Fispur`. |

The CLI just `ProjectReference`s the `Fispur` project and exposes it over UCI.
The root namespace of the `Fispur` project is **`FispurEngine`** (not `Fispur`) —
`Fispur` is the WIP bot class, so the namespace has to differ from it.

## Where the code lives (`Fispur/src/`)

- **`API/`** — the restricted bot-facing API (namespace `FispurEngine.API`):
  `Board`, `Move`, `Timer`, `Piece`, `Square`, `PieceList`, `PieceType`,
  `BitboardHelper`, and the `IChessBot` interface. Bots are written against
  *this*, not the full engine.
- **`Framework/Chess/`** — the actual chess engine (namespace `FispurEngine.Chess`):
  `Board`, `MoveGenerator` (magic bitboards under `Move Generation/Magics` +
  `Bitboards`), `Zobrist`, `RepetitionTable`, FEN/PGN helpers, `Arbiter`/result logic.
- **`Framework/Application/`** — the Raylib GUI + match runner (namespace
  `FispurEngine.Application`): `Core/Program.cs` (GUI entry point),
  `ChallengeController`, `Settings.cs`, players, UI, `Token Counter`.
- **`Fispur-New/Fispur.cs`** (+ `NNUE.cs`) — the **current WIP engine**
  (class `Fispur`, namespace `FispurEngine`). This is the one to edit when
  improving the engine.

## Current engine techniques (STALE — describes v0.2.1)

> ⚠ Out of date: the WIP engine is at 0.19.0 and uses an NNUE eval
> (`Fispur-New/NNUE.cs`, net embedded via `Training/Nets/10kb-1024hl-8ob.bin`).
> Re-read the source before relying on this section.

Negamax **alpha-beta** with **quiescence search**, **iterative deepening**,
a Zobrist-keyed **transposition table**, MVV-LVA-style **move ordering**
(TT move first, then captures by victim/attacker value), and a simple
**material-only eval** (`pieceVal = {100,300,350,500,900,10000}`). Time
management: spends roughly `remaining/20 + increment/2` ms per move.

## UCI CLI notes

`Fispur-cli-(exp)/Program.cs` implements `uci` / `isready` / `ucinewgame` /
`position [startpos|fen ...] [moves ...]` / `go` / `quit`. Quirk: `go movetime X`
is passed to the engine as `X * 12`. `bestmove` is parsed out of `Move.ToString()`
via a substring. `ucinewgame` reflectively re-news the bot instance.

## Supporting folders

- **`Openings/`** — `UHO_4060_v2.epd` (opening/test positions, ~15 MB),
  `startPos` (start FEN).
- **`Testing/`** — `imgui.ini`. SPRT patch testing runs through OpenBench.
- **`Fispur/resources/`** — `Fens.txt`, `Pieces.png`, `Fonts/` (GUI assets).
- `.github/copilot-instructions.md` currently only has Azure boilerplate (ignore).

## Build / run

- Requires .NET SDK 10 (both projects target `net10.0`). VS 2022 17.14 builds it with warning NETSDK1233; VS 2026 / Rider / `dotnet` CLI are fine.
- GUI: run the `Fispur` project (Raylib window).
- Engine for a UCI GUI/tester: build/run `Fispur-cli-(exp)`.
- OpenBench: `make EXE=<name>` in the repo root publishes `Fispur-cli-(exp)` as a
  single self-contained `<name>.exe` — that is the exact contract the OpenBench
  worker expects. The `OB_EXE` property drives the rename and switches off the
  GUI's asset copies. The publish goes to `ob-publish/` and a Copy target in
  `Fispur-cli-(exp).csproj` puts the binary into the root — never publish with
  `-o .` (SDK 10.0.2xx+ excludes PublishDir/** from compilation, i.e. every project). Server/worker
  setup is documented in `Docs/OpenBench-Setup.md`.

## Conventions

- New engine work goes in `Fispur-New/Fispur.cs`. When cutting a
  release, bump `GetName` — no source snapshot is archived.
- Git: work on `main`; commits are short and version-oriented
  (e.g. "Implement basic transposition table").
