# Spiritbreaker — Project Memory

Chess engine written in C# by **Xenymor**. Built on top of Sebastian Lague's
"Chess Coding Challenge" framework (the `API` + `Framework` code is his;
Spiritbreaker adds the engine logic and the UCI CLIs). This file is Claude's
project memory — read it first to orient before diving into files.

## What this is

- A UCI-compatible chess engine (`GetName` / `GetAuthor` / `Think(board, timer)`).
- Ships two things: a **Raylib GUI** (Lague's challenge app, for watching bots
  play) and two **UCI command-line wrappers** so the engine can run in any UCI
  GUI / tester (cutechess, OpenBench, etc.).
- The engine is developed as a series of versioned snapshots, so old strength
  levels stay runnable for regression testing.

## Solution layout (`Spiritbreaker.sln`, VS 2022)

Three projects:

| Project | TFM | Role |
|---|---|---|
| `Spiritbreaker` | net6.0 | The framework + all bot code + the Raylib GUI. Depends on `Raylib-cs 4.5` and `Microsoft.CodeAnalysis` (token counter). This is where all real code lives. |
| `Spiritbreaker-cli` | net8.0 | UCI wrapper that runs the **old/versioned** line — `SpiritBreaker0_2_0`. |
| `Spiritbreaker-New-cli` | net8.0 | UCI wrapper that runs the **current/active** engine — `SpiritBreaker` (v0.2.1). |

Both CLIs just `ProjectReference` the `Spiritbreaker` project and expose it over UCI.

## Where the code lives (`Spiritbreaker/src/`)

- **`API/`** — the restricted bot-facing API (namespace `Spiritbreaker.API`):
  `Board`, `Move`, `Timer`, `Piece`, `Square`, `PieceList`, `PieceType`,
  `BitboardHelper`, and the `IChessBot` interface. Bots are written against
  *this*, not the full engine.
- **`Framework/Chess/`** — the actual chess engine (namespace `Spiritbreaker.Chess`):
  `Board`, `MoveGenerator` (magic bitboards under `Move Generation/Magics` +
  `Bitboards`), `Zobrist`, `RepetitionTable`, FEN/PGN helpers, `Arbiter`/result logic.
- **`Framework/Application/`** — the Raylib GUI + match runner (namespace
  `Spiritbreaker.Application`): `Core/Program.cs` (GUI entry point),
  `ChallengeController`, `Settings.cs`, players, UI, `Token Counter`.
- **`SpiritBreaker/`** — the **versioned bot snapshots**: `SpiritBreaker-0.0.2`
  → `0.0.3` → `0.1.1` → `0.2.0` (class names like `SpiritBreaker0_2_0`), plus
  `EvilBot.cs` (namespace `Spiritbreaker.Example`) — a fixed reference opponent.
- **`SpiritBreaker-New/SpiritBreaker.cs`** — the **current WIP engine** (v0.2.1,
  class `SpiritBreaker`, global namespace). This is the one to edit when
  improving the engine.

## Current engine techniques (v0.2.1)

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

`Spiritbreaker-cli` additionally exposes a `Version` UCI option
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
- **`Spiritbreaker/resources/`** — `Fens.txt`, `Pieces.png`, `Fonts/` (GUI assets).
- `.github/copilot-instructions.md` currently only has Azure boilerplate (ignore).

## Build / run

- Requires .NET SDK (6.0 for the GUI project, 8.0 for the CLIs) — targeted via VS 2022.
- GUI: run the `Spiritbreaker` project (Raylib window).
- Engine for a UCI GUI/tester: build/run `Spiritbreaker-New-cli` (current) or
  `Spiritbreaker-cli` (v0.2.0).

## Conventions

- New engine work goes in `SpiritBreaker-New/SpiritBreaker.cs`. When cutting a
  release, snapshot it into `SpiritBreaker/SpiritBreaker-X.Y.Z.cs` with a
  version-suffixed class name and bump `GetName`.
- Git: work on `main`; commits are short and version-oriented
  (e.g. "Implement basic transposition table").
