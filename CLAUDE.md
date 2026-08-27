# CLAUDE.md

Guidance for Claude (and any other agent or contributor) working in this
repository. Adapted from the Karpathy-inspired guidelines at
[multica-ai/andrej-karpathy-skills](https://github.com/multica-ai/andrej-karpathy-skills)
for this project specifically.

## What this is

**Meeting Recorder & Speaker Marker** — a Windows 11 desktop app. One PC
sits in a meeting room, records the whole meeting to a single MP3, and lets
one operator mark who is speaking by clicking a tile or pressing a number
key. Output is that MP3 plus a Markdown file of speaker segments and
timestamp gaps, meant to be handed to an LLM to produce a per-speaker
transcript. Offline only — no accounts, no network calls, no cloud sync.

The full spec is [`docs/recorder-design-guide.html`](docs/recorder-design-guide.html)
(an archived copy of the `claude.ai/design` doc this was built from — read
it as text, don't open it in a browser, see the comment at its top).
**Read it before changing behaviour, not just this file** — this file
summarises the decisions that affect how you build, not every screen and
interaction rule.

> The original written brief described an Android phone app; the design
> guide resolved that to **Windows 11 desktop only** — desktop wins, every
> phone-specific assumption is dropped. If a task ever points back at
> "Android," treat that as stale and confirm before acting on it.

## Working principles

1. **Think before coding.** State assumptions out loud. If a request has
   more than one reasonable reading, ask or name the interpretation you're
   going with — don't silently guess on anything user-facing (mark timing,
   file formats, the Markdown contract in particular, see below).
2. **Simplicity first.** Ship the minimal code that satisfies the stated
   requirement. Don't build toward "Scope of this build — deferred" items
   speculatively; that list exists so they're deliberate follow-ups, not
   gaps to quietly fill in.
3. **Surgical changes.** Touch only what the task requires. Match existing
   style; don't reformat or "clean up" unrelated code in the same commit.
4. **Goal-driven execution.** Turn a task into a checkable outcome:
   `[change] → verify: [how to confirm it worked]`. For this repo,
   "verify" almost always means "the CI build in
   `.github/workflows/build-windows-exe.yml` produces `MeetingRecorder.exe`"
   since a WPF app can't be run or built headless in this Linux dev
   environment — see below.

## Tech stack

- **.NET 8, WPF, C#.** The design guide's own "Build notes" section
  suggests WinUI 3 + MSIX packaging; this repo uses **WPF + a
  self-contained single-file exe instead**, because the task's own
  requirement — one standalone `.exe`, no pre-install setup — conflicts
  with MSIX (which needs either Store distribution or enabling Windows
  sideloading/dev mode plus a trusted certificate, i.e. *more* setup, not
  less). The design guide explicitly allows this substitution ("If WinUI 3
  is not viable for you, ask before switching to WPF — the layout survives
  either, the dependency story does not"); this was confirmed with the
  user before building. Keep this decision when extending the app — don't
  reintroduce WinUI3/MSIX to chase visual fidelity with the guide.
- **NAudio** for capture (`WaveInEvent`) and **NAudio.Lame** for MP3
  encoding (bundles `libmp3lame.dll`, x86+x64, as content — no ffmpeg
  process, no runtime download, matching the design guide's build note).
- Layout: `src/MeetingRecorder/`
  - `Models/` — `Speaker`, `Mark`, `RecordingSession`. Plain data, no logic.
  - `Services/` — `AudioCaptureService` (capture + MP3 encode + file-time
    tracking), `InputLevelMeter` (setup-screen mic check, no file written),
    `MarkingEngine` (the tap-toggle marking state machine, pure logic, no
    UI dependency), `MarkdownExporter` (writes the output contract).
  - `Views/` — `SetupWindow`, `RecordingWindow`, `ExportSummaryWindow`. One
    WPF `Window` per screen; UI built mostly in code-behind rather than
    heavy XAML data-binding, to keep behaviour easy to trace in one place.

## Scope of this build — implemented vs. deferred

Implemented: **Setup → Record & Mark → Stop & Export**, the spec's core
vertical slice, confirmed with the user before building instead of the
full 5-screen spec in one pass. Covers: roster + input device setup with a
live level check; tap-toggle marking with the 0.8s backdate offset and the
1.2s double-tap reopen repair (design guide section 09); Ctrl+Z undo,
Ctrl+P pause (cuts and rejoins the audio), Esc stop with an inline (not
modal) confirmation; the exact Markdown output contract (section 10);
elapsed time driven by audio samples written, not the wall clock (section
11).

**Deliberately deferred — do not build these speculatively, confirm scope
with the user first:**

- **S1 Library** (past-session browsing, crash recovery entry point) — no
  session list exists yet; each run starts a fresh meeting.
- **Global hotkeys + mini-bar toast** (Alt+1…0 working while another app
  has focus) — needs `RegisterHotKey` + a tray icon; marking only works
  while the app window has focus right now.
- **Preset management** (save/load a named roster) — roster is re-entered
  every session.
- **Live mark editing** (nudge/split/merge/reassign in an expanded marks
  dock, audition playback) — the marks dock here is read-only, last 8
  marks. Undo is a single linear stack, not the guide's "unlimited depth,"
  and there's no redo (Ctrl+Y) yet.
- **Crash recovery journal** (`marks.jsonl`, `session.json`, fsync per
  operation) — nothing is persisted until Stop; a crash mid-meeting loses
  the session. This is the biggest gap vs. "non-negotiable behaviour" in
  section 11 and should be prioritised in the first follow-up.
- **Device-unplugged fallback**, **sleep/display-off inhibition**, **disk
  free-space display**, **"allow overlapping marks" setting** — not
  implemented; marking always closes the previous speaker (overlap off).
- **Visual fidelity** — the dark theme's colour tokens (section 02) are
  used as-is, but layout is a functional approximation built in code, not
  a pixel-perfect recreation of the mockup, and it substitutes system
  fonts (Consolas/Segoe) for the guide's Inter/JetBrains Mono rather than
  bundling font files.

## Output contract (read section 10 before touching `MarkdownExporter`)

The Markdown file is written for an LLM to consume, not a human — the
design guide calls it "a contract, not a suggestion." Field order, the
separate `## Gaps` table, and the `HH:MM:SS.mmm` timestamp format (offset
from the start of `audio_file`, not wall-clock) are all load-bearing.
Don't reshape this without re-reading section 10 and confirming — the
guide itself flags the format as *its* proposal, not a settled requirement,
so if it needs to change, that's a real decision, not a refactor.

## Windows build & packaging

**The deliverable for Windows users is one `.exe` file, self-contained, no
setup.** This is a project requirement, not a default to optimize away.

```powershell
dotnet publish src/MeetingRecorder/MeetingRecorder.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

This is already encoded in `MeetingRecorder.csproj` (`SelfContained`,
`PublishSingleFile`, `RuntimeIdentifiers=win-x64`) and in the CI workflow —
don't remove those properties to "simplify" the project file.

**Prerequisites, precisely:**

- *To run the built `.exe`:* nothing. Self-contained — the .NET runtime
  and the MP3 encoder are embedded in the file. Only requirement is
  64-bit Windows 10/11. Don't add a step asking end users to install the
  .NET runtime, or to install ffmpeg for MP3 — both defeat the point.
- *To build from source:* the .NET 8 SDK, and Windows (or CI's
  `windows-latest`) — WPF's reference assemblies for `net8.0-windows` only
  exist on Windows. This dev sandbox is Linux, so builds here are
  validated by reading the code and by CI, not by running `dotnet build`
  locally.
- *One unavoidable rough edge:* the exe is unsigned, so first launch shows
  a Windows SmartScreen warning. There's no free way to remove that — tell
  users **"More info" → "Run anyway."** Don't add registry tweaks or
  unblock scripts asking users to lower their system's security settings.

CI (`.github/workflows/build-windows-exe.yml`) publishes this exe on every
push to `main` and uploads it as a build artifact; pushing a `v*` tag also
attaches it to a GitHub Release. If you change build flags, make sure that
workflow still produces a working single-file exe — it's the actual proof
the packaging requirement is met, since it can't be verified locally here.

## Repo conventions

- Default branch: `main`. Feature work happens on branches, opened as PRs
  into `main` — see the repo's CI checks before merging.
- Keep commits scoped to one logical change with a clear message.
- Don't commit recordings, session output, or `bin/`/`obj/`/`publish/` —
  see `.gitignore`.
