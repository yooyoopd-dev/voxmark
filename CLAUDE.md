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
  - `Theme/` — `Tokens.xaml` (the section 02 colour tokens), `Controls.xaml`
    (button/input/scrollbar skins), `Palette.cs` (the same tokens for the
    code-built parts: tiles, waveform, block lane). Keep the XAML and the
    C# palette in step.
  - `Models/` — `Speaker`, `MarkKey`, `Mark`, `OpenMark`, `Gap`,
    `SessionOptions`, `Preset`, `RecordingSession`. Plain data, no logic;
    `RecordingSession` is what `session.json` serialises.
  - `Services/` — `AudioCaptureService` (capture, MP3 encode, file-time
    tracking, device fallback), `InputLevelMeter` (setup mic check, no file
    written), `MarkingEngine` (the tap-toggle state machine *and* the live
    repair operations — pure logic, no UI dependency), `MarkJournal`
    (`marks.jsonl`, fsync per operation), `SessionStore` / `PresetStore`
    (the on-disk session folder and presets), `MarkdownExporter` (the output
    contract), `GlobalHotkeyService`, `PowerKeepAwake`, `KeyMap`, `DiskInfo`.
  - `Controls/` — `TitleBar`, `WaveformView`, `BlockLaneView`, `SpeakerTile`,
    `RosterRow`, `MarksDock`, `Dropdown`. Custom-drawn where the guide asks
    for something WPF has no primitive for.
  - `Views/` — `ShellWindow` (shared 40px chrome), `LibraryWindow` (S1),
    `SetupWindow` (S2), `RecordingWindow` (S3+S4), `ExportWindow` (S5),
    `ToastWindow` (the mini bar), `Ui` (small layout builders). UI is built
    in code rather than XAML data-binding, to keep behaviour traceable in
    one place.

## Scope of this build — implemented vs. deferred

All five screens in the guide are built: **S1 Library → S2 Setup → S3 Record
& mark → S4 Marks dock → S5 Stop & export.**

Implemented, with the guide section each answers to:

- **02 tokens** — the colour ramp, the 12-slot speaker palette, monospaced
  tabular timecodes, 4px spacing scale, 72×160 minimum tile.
- **04 recording** — live 45 s waveform with chapter-marker flags at every
  speaker change, whole-session minimap with the live viewport, the
  Marks / Speaking now / Input / Written to disk header, dropped-buffer
  count surfaced rather than swallowed.
- **05 grid** — column count and tile height derived from speaker count
  alone (2→4 / 5–6 / 7–9 / 10–12), roster order fixed for the session.
- **06 marks dock** — collapsed live-repair rows and the expanded dock:
  filters, inline reassign, 0.5 s / 0.1 s nudges, split at the review
  playhead, merge, insert-into-gap, immediate delete with a 6 s undo toast,
  neighbour trimming with a notice, and self-flagged suspects (marks under
  2 s, sub-0.3 s gaps between different speakers).
- **07 setup & library** — two-pane setup with meeting metadata, presets,
  the unskippable input check with a dB scale and free-disk readout, roster
  rows with per-speaker key cells (duplicates rejected at keystroke time),
  absent speakers that keep their slot, overlap and mark-offset options; the
  library lists past sessions and offers one-click recovery.
- **08 stop & export** — inline stop confirmation (never a modal), auto-close
  of the open mark, the finalise-pass export screen with per-speaker talk
  time and an honest "Unmarked" row.
- **09 keyboard** — 1–9/0 and Shift+1/2, Space, Ctrl+Z/Ctrl+Y (unlimited,
  snapshot-based), Ctrl+P, Ctrl+E, Ctrl+N, ↑↓/Enter/Tab/←→, Esc; plus
  **Alt+1…0 / Alt+Shift+1,2 global hotkeys** with the 2-second, focus-free
  mini-bar toast, and refused registrations named in the header.
- **10 output contract** — unchanged in shape; see below.
- **11 non-negotiables** — `marks.jsonl` fsync'd per operation, sleep and
  display-off inhibited, device-unplugged fallback that keeps recording and
  writes a note into the Markdown, timestamps from the audio sample count.

**Deliberately deferred — confirm scope with the user before building:**

- **Audition playback** in the marks dock. The guide wants it enabled only
  with headphones, and there is no reliable way to detect them; the button
  ships in the guide's own disabled state with its "connect headphones"
  label rather than risking playback feeding back into the recording.
- **Bundled Inter / JetBrains Mono.** The guide names those faces; shipping
  font files fights the "one small self-contained exe" requirement, so the
  app substitutes Segoe UI Variable and Cascadia Mono with fallbacks.
- **System-audio capture as a second lane.** The guide puts it out of scope;
  the pre-existing loopback *device* option is kept, and the true format is
  written into `audio_format` rather than assumed.
- **WinUI 3 / MSIX** — see the tech stack note above. Not a gap to close.
- **Shared or multi-machine libraries**, calendar/Teams integration,
  diarisation, audio editing — all out of scope per section 11.

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
