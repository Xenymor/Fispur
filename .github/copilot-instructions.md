# Copilot instructions — Spiritbreaker

Spiritbreaker is a UCI chess engine in C# by Xenymor, built on Sebastian
Lague's "Chess Coding Challenge" framework. See `CLAUDE.md` in the repo root
for the full project map; this file is the short version for code completion.

## Structure

- Solution `Spiritbreaker.sln` has three projects:
  - `Spiritbreaker` (net6.0) — framework, engine, Raylib GUI, and all bot code.
  - `Spiritbreaker-cli` (net8.0) — UCI wrapper for the old bot `SpiritBreaker0_2_0`.
  - `Spiritbreaker-New-cli` (net8.0) — UCI wrapper for the current engine `SpiritBreaker` (v0.2.1).
- Key namespaces: `Spiritbreaker.API` (bot-facing `Board`/`Move`/`Timer`/`IChessBot`),
  `Spiritbreaker.Chess` (real engine: magic-bitboard movegen, Zobrist, FEN/PGN),
  `Spiritbreaker.Application` (GUI + match runner + `Settings`).

## Where to write code

- Active engine work goes in `Spiritbreaker/src/SpiritBreaker-New/SpiritBreaker.cs`
  (class `SpiritBreaker`, implements `IChessBot`).
- Do NOT edit the versioned snapshots in `Spiritbreaker/src/SpiritBreaker/`
  (`SpiritBreaker-0.x.y.cs`, `EvilBot.cs`) — they are frozen for regression testing.
- When releasing, snapshot the current engine into
  `SpiritBreaker/SpiritBreaker-X.Y.Z.cs` with a version-suffixed class name and
  bump the string in `GetName`.

## Engine conventions

- Bots implement `IChessBot`: `GetAuthor()`, `GetName()`, `(Move, int eval) Think(Board, Timer)`.
- Write against the restricted `Spiritbreaker.API` types, not the full `Spiritbreaker.Chess` engine.
- Current search: negamax alpha-beta + quiescence, iterative deepening,
  Zobrist-keyed transposition table, MVV-LVA move ordering, material-only eval.
- Evals are in centipawns (`pieceVal = {100,300,350,500,900,10000}`); mate scores
  are offset by ply.
- Time budget target is about `timer.MillisecondsRemaining/20 + increment/2` ms per move.

## UCI notes (the `*-cli` Program.cs)

- Implements `uci`, `isready`, `ucinewgame`, `position [startpos|fen ...] [moves ...]`, `go`, `quit`.
- `go movetime X` is passed to the engine as `X * 12` (intentional).
- `ucinewgame` reflectively re-instantiates the bot.

## Build

- .NET SDK required (6.0 for the GUI project, 8.0 for the CLIs); developed in Visual Studio 2022.
- Prefer `Raylib-cs` (already referenced) for GUI work; don't add new heavy dependencies without reason.
