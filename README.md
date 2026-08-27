# Meeting Recorder & Speaker Marker

A Windows 11 desktop app for one specific job: one PC sits in a meeting
room, records the whole thing to a single MP3, and lets one operator mark
who is speaking by clicking a tile or pressing a number key. On Stop it
writes an MP3 + a Markdown file of speaker segments and timestamp gaps —
handed to an LLM to produce a per-speaker transcript. Nothing else.

Offline only — no accounts, no network calls, no cloud sync.

Full behavioural spec lives in the design guide this was built from
(`docs/recorder-design-guide.html`, or ask for the original
`claude.ai/design` link). Development conventions and what's implemented
vs. deferred are in [`CLAUDE.md`](CLAUDE.md).

## Run from source (Windows only)

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet run --project src/MeetingRecorder
```

## Build the standalone .exe

```powershell
dotnet publish src/MeetingRecorder/MeetingRecorder.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

The result, `publish/MeetingRecorder.exe`, is the entire deliverable — copy
that one file anywhere on a 64-bit Windows 10/11 machine and run it. No
.NET install, no ffmpeg, no extra DLLs to place alongside it (the MP3
encoder is bundled). See [`CLAUDE.md`](CLAUDE.md) for the one prerequisite
that can't be removed: the exe is unsigned, so first run shows a
SmartScreen prompt (More info → Run anyway).

Every push to `main` and every `v*` tag also builds this exe automatically
via [`.github/workflows/build-windows-exe.yml`](.github/workflows/build-windows-exe.yml);
tagged builds are attached to a GitHub Release.

## Using it

1. **Library** — every past meeting is a folder on this machine. Start a new
   one, or finish exporting a session the app never got to close.
2. **Setup** — title the meeting, pick the input device and watch the level
   meter, build the roster. Each speaker gets a marking key (1-9, 0, then
   Shift+1/Shift+2) that you can reassign by clicking its key cell; absent
   people keep their slot and colour. Save the roster as a preset to reuse it.
3. **Record & mark** — press a speaker's key (or click their tile) when they
   start; press another speaker's key to hand over in one boundary. Every
   mark start is backdated 0.8 s, because a human always presses late.

   | | |
   |---|---|
   | `1`…`9`, `0` | mark speakers 1-10 |
   | `Shift+1` / `Shift+2` | speakers 11-12 |
   | `Alt+1`…`0` | the same, from any other app — a toast confirms |
   | `Space` | close the open mark without opening another |
   | `Ctrl+Z` / `Ctrl+Y` | undo / redo, unlimited |
   | `Ctrl+P` | pause — the audio is cut and rejoined |
   | `Ctrl+E` | expand the marks dock to repair marks live |
   | `Ctrl+N` | add a speaker mid-meeting |
   | `↑` `↓` `Enter` | move and open a mark in the dock |
   | `←` `→` (`Shift` for 0.1 s) | nudge the selected boundary 0.5 s |
   | `Esc` | stop — asks once, inline |

   The dock flags its own suspects: marks under 2 seconds, and sub-0.3-second
   gaps between two different speakers. Recording never stops while you edit.
4. **Stop** — writes `<slug>_<date>.mp3` and `<slug>_<date>.md` into
   `Documents\VoxMark\Sessions\<date>_<slug>\`, alongside a `session.json`
   and the `marks.jsonl` journal that makes a crash recoverable.

## What's implemented vs. deferred

All five screens in the design guide are built — library, setup, record,
marks dock, export — along with the global hotkeys, the crash-recovery
journal, presets and live mark repair.

Two things are deliberately left out, and both are judgement calls rather
than gaps: **audition playback** in the marks dock (the guide only wants it
with headphones attached, which cannot be detected reliably, so the button
ships in its documented disabled state) and **bundled Inter / JetBrains
Mono** (shipping font files fights the one-small-exe requirement, so the app
substitutes Segoe UI Variable and Cascadia Mono). See "Scope of this build"
in [`CLAUDE.md`](CLAUDE.md) for the full list and the reasoning.
