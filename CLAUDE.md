# Fispur — Project Memory

Chess engine written in C# by **Xenymor**. Built on top of Sebastian Lague's
"Chess Coding Challenge" framework (the `API` + `Framework` code is his;
Fispur adds the engine logic and the UCI CLIs). This file is Claude's
project memory — read it first to orient before diving into files.

## What this is

- A UCI-compatible chess engine (`GetName` / `GetAuthor` / `Think(board, timer)`).
- Ships two things: a **Raylib GUI** (Lague's challenge app, for watching bots
  play) and two **UCI command-line wrappers** so the engine can run in any UCI
  GUI / tester (cutechess, OpenBench, etc.).
- The engine is developed as a series of versioned snapshots, so old strength
  levels stay runnable for regression testing.

## Solution layout (`Fispur.sln`, VS 2022)

> The repository root folder on disk is still named `Spiritbreaker` (the project
> was renamed from Spiritbreaker to Fispur; the folder was left alone so the
> absolute paths in `Testing/*.qsprt` / `*.qtour` keep working).

Three projects:

| Project | TFM | Role |
|---|---|---|
| `Fispur` | net6.0 | The framework + all bot code + the Raylib GUI. Depends on `Raylib-cs 4.5` and `Microsoft.CodeAnalysis` (token counter). This is where all real code lives. |
| `Fispur-cli` | net8.0 | UCI wrapper that runs the **archived/versioned** line — any snapshot via the `Version` UCI option (default `0.10.0`). |
| `Fispur-cli-(exp)` | net8.0 | UCI wrapper that runs the **current/WIP** engine — class `Fispur`. |

Both CLIs just `ProjectReference` the `Fispur` project and expose it over UCI.
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
- **`Fispur/`** — the **versioned bot snapshots**: `0.0.2` → `0.0.3` → `0.1.1`
  → `0.2.0` → `0.2.1` as single files (`Fispur-X.Y.Z.cs`), and `0.3.0` … `0.10.0`
  as folders (`Fispur-X.Y.Z/` with their own `NNUE.cs`). Class names are
  `Fispur0_2_0`, `Fispur0_10_0`, …; namespaces `FispurEngine.Fispur0_X_Y`.
  Plus `EvilBot.cs` (namespace `FispurEngine.Example`) — a fixed reference opponent.
- **`Fispur-New/Fispur.cs`** (+ `NNUE.cs`) — the **current WIP engine**
  (class `Fispur`, namespace `FispurEngine`). This is the one to edit when
  improving the engine.

## Current engine techniques (STALE — describes v0.2.1)

> ⚠ Out of date: the WIP engine is at 0.10.0 and uses an NNUE eval
> (`Fispur-New/NNUE.cs`, net embedded via `Training/Nets/net0.3.0.bin`).
> Re-read the source before relying on this section.

Negamax **alpha-beta** with **quiescence search**, **iterative deepening**,
a Zobrist-keyed **transposition table**, MVV-LVA-style **move ordering**
(TT move first, then captures by victim/attacker value), and a simple
**material-only eval** (`pieceVal = {100,300,350,500,900,10000}`). Time
management: spends roughly `remaining/20 + increment/2` ms per move.

## UCI CLI notes

`Program.cs` in each `*-cli` implements `uci` / `isready` / `ucinewgame` /
`position [startpos|fen ...] [moves ...]` / `go` / `quit`. Quirk: `go movetime X`
is passed to the engine as `X * 12`. `bestmove` is parsed out of `Move.ToString()`
via a substring. `ucinewgame` reflectively re-news the bot instance.

`Fispur-cli` additionally exposes a `Version` UCI option
(`option name Version type combo default 0.9.0 var 0.0.2 ... var 0.9.0`).
`setoption name Version value 0.5.1` swaps the running bot to that snapshot
(lazily instantiated), re-applies the current `Hash` and resets the board.
The version registry lives in `Program.cs` (`Versions` array) - add a line
there when a new snapshot is archived. Hash/`NewGame` support is probed by
reflection, so pre-0.7.0 snapshots (which have neither) still work.

## Supporting folders

- **`Openings/`** — `UHO_4060_v2.epd` (opening/test positions, ~15 MB),
  `startPos` (start FEN).
- **`Testing/`** — `defaultPatchTest.qsprt` (SPRT test config for engine
  patch testing), `imgui.ini`.
- **`Fispur/resources/`** — `Fens.txt`, `Pieces.png`, `Fonts/` (GUI assets).
- `.github/copilot-instructions.md` currently only has Azure boilerplate (ignore).

## Build / run

- Requires .NET SDK (6.0 for the GUI project, 8.0 for the CLIs) — targeted via VS 2022.
- GUI: run the `Fispur` project (Raylib window).
- Engine for a UCI GUI/tester: build/run `Fispur-cli-(exp)` (current WIP) or
  `Fispur-cli` (archived snapshots, selectable via the `Version` option).

## Conventions

- New engine work goes in `Fispur-New/Fispur.cs`. When cutting a
  release, snapshot it into `Fispur/Fispur-X.Y.Z.cs` with a
  version-suffixed class name and bump `GetName`.
- Git: work on `main`; commits are short and version-oriented
  (e.g. "Implement basic transposition table").
