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

1. **Setup** — title the meeting, pick the input device (check the level
   bar), add speakers (each gets a key: 1-9, 0, then Shift+1/Shift+2).
2. **Start recording** — press a speaker's key (or click their tile) to
   mark them as speaking; press it again, or another speaker's key, to
   switch. Space closes without opening anyone. Ctrl+Z undoes, Ctrl+P
   pauses (cuts the audio and rejoins it on resume), Esc stops (asks once).
3. **Stop** — writes `<slug>_<date>.mp3` and `<slug>_<date>.md` into
   `Documents\MeetingRecorder\Sessions\<date>_<slug>\`.

## What's implemented vs. deferred

This build is the core vertical slice — setup → record & mark → export —
not the full design spec. See "Scope of this build" in
[`CLAUDE.md`](CLAUDE.md) for exactly what's deferred (session library,
global hotkeys, preset management, live mark editing, crash recovery) and
why.
