# CLAUDE.md

Guidance for Claude (and any other agent or contributor) working in this
repository. Adapted from the Karpathy-inspired guidelines at
[multica-ai/andrej-karpathy-skills](https://github.com/multica-ai/andrej-karpathy-skills)
for this project specifically.

## What this is

**VoxMark** — a Windows 11 desktop app, called "Meeting Recorder & Speaker
Marker" in the design guide below; VoxMark is the shipping name, and the one
the assembly, the exe and the `Documents\VoxMark\` folder all use. One PC
sits in a meeting room, records the whole meeting to a single MP3, and lets
one operator mark who is speaking by clicking a tile or pressing a number
key. Output is that MP3 plus a Markdown file of speaker segments and
timestamp gaps, meant to be handed to an LLM to produce a per-speaker
transcript. The full edition can also recognise the speech itself, on the
same machine, and writes the words into that Markdown under the speaker the
operator marked. Offline only — no accounts, no network calls, no cloud
sync; speech recognition included, see "Speech recognition" below.

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
   `.github/workflows/build-windows-exe.yml` produces `VoxMark.exe`"
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
- **Whisper.net 1.8.1** for speech recognition, in the Full edition only.
  See "Speech recognition" below for why it is not faster-whisper and why
  the version is pinned.
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
    contract), `GlobalHotkeyService`, `PowerKeepAwake`, `KeyMap`, `DiskInfo`,
    `BuildProfile` (which edition this exe is), `AppSettingsStore` (the
    app-wide settings, in `%LocalAppData%` — see "Settings vs. Setup").
    Speech recognition adds `WhisperRuntime` (finds the natives and the model
    file) and `TranscriptionService` (the pipeline) — **the only two files
    that name a Whisper.net type, and the only two Lite removes from
    compilation**; plus `TranscriptStore` (`transcript.jsonl`, fsync per
    segment), `TranscriptMapper` (segment → mark attribution) and
    `TranscriptionSettingsStore`, all of which build into both editions so
    Lite can still open and export a session Full recorded.
  - `Controls/` — `TitleBar`, `WaveformView`, `BlockLaneView`, `SpeakerTile`,
    `RosterRow`, `MarksDock`, `Dropdown`, `TranscriptView`. Custom-drawn
    where the guide asks for something WPF has no primitive for.
  - `Views/` — `ShellWindow` (shared 40px chrome), `LibraryWindow` (S1),
    `SetupWindow` (S2), `RecordingWindow` (S3+S4), `ExportWindow` (S5),
    `SettingsWindow` (the app-wide settings dialog, see below),
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
- **Speech recognition (Full edition)** — beyond the guide: an opt-in
  on-device whisper pass, a three-line live transcript strip under the
  minimap, and a `## Transcript` section mapping the words onto the marks.
  See "Speech recognition" and "Editions" below.

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
- **Bundling a speech model, or downloading one.** Not a gap either: the
  model is deliberately the operator's own local file. See "Speech
  recognition" below.
- **Shared or multi-machine libraries**, calendar/Teams integration,
  diarisation, audio editing — all out of scope per section 11.

## Speech recognition

An addition on top of the design guide, not something it asked for. It is
governed by one rule that outranks every other consideration here — section
11's **recording never stops for any reason**. Transcription is a passive tap
on the audio: the capture thread only copies samples into a queue, everything
expensive or fallible happens on a worker thread that cannot reach the
encoder, and every failure path (no model, no runtime, a whisper crash, a
lost GPU, falling behind) degrades to "no text" plus a sentence in the UI.
**If a change here could ever make a recording fail, it is the wrong
change.**

### Whisper.net, not faster-whisper

The request that started this named
[faster-whisper](https://github.com/SYSTRAN/faster-whisper). It is a Python
library on CTranslate2, so shipping it means either embedding a Python
runtime plus cuDNN (~2–3 GB) or telling users to `pip install` first — and
that collides head-on with this repo's one-exe, no-setup requirement.
**Whisper.net** runs the same Whisper models through whisper.cpp, in-process,
as a NuGet, and survives `PublishSingleFile`. This was confirmed with the
user before building; don't "fix" it back to faster-whisper.

**Pinned to 1.8.1 deliberately.** On `net8.0`, 1.8.1 has *zero* managed
dependencies; 1.9.x pulls `Microsoft.Extensions.AI.Abstractions` and
`System.Text.Json 10` into what is otherwise a two-package app. Every API
this code uses is identical in both. Upgrade only with a reason.

### The model is always a local file

There is no download button, no cache fetch and no first-run step. The
operator supplies a ggml `.bin`, which VoxMark finds in
`Documents\VoxMark\Models\` or via Browse. This is not laziness — the target
machine sits behind a filtering corporate proxy, and it is also the only way
"offline only — no network calls" stays literally true. **Do not add a
downloader.**

### Finding the natives is the fragile part

`Whisper.net`'s loader does not P/Invoke by name: it walks the disk for
`<root>/runtimes/win-x64/whisper.dll` and loads it by path. In a single-file
exe those files are wherever the host extracted them, so `WhisperRuntime.Probe`
searches candidate roots (`NATIVE_DLL_SEARCH_DIRECTORIES` is the one that
matters for a published exe) and hands the answer over via
`RuntimeOptions.LibraryPath`. That is also why the Full build sets
`IncludeAllContentForSelfExtract` — it is what keeps the `runtimes/...` tree
intact instead of flattening it. If transcription ever stops finding its
engine, start there, and remember `Documents\VoxMark\whisper-runtime\` is the
manual override.

### Timestamps

Segment times must land on the same timebase as the marks or the whole
feature is decorative. Audio is consumed strictly in order and nothing is
dropped silently, so a chunk's start is exactly
`consumedSourceFrames / sourceSampleRate` — the same "count the samples that
were written" rule `AudioCaptureService` uses for file time. Chunks are
resampled to 16 kHz one at a time rather than as a continuous stream, which
costs a few ms of filter warm-up per boundary (inaudible to a decoder) and
buys exactly this property. Don't replace it with a streaming resampler
without solving the drift it reintroduces.

## Editions — Lite and Full

Two exes from this one source tree, selected by an MSBuild property:

```powershell
-p:Edition=Full   # default: whisper.cpp + CUDA, 229 MB, compressed
-p:Edition=Lite   # no speech recognition, 157 MB, today's recorder exactly
```

Lite defines `VOXMARK_LITE`, references none of the Whisper packages, and
`<Compile Remove>`s `WhisperRuntime.cs` and `TranscriptionService.cs`. Its
Setup screen has no transcription row at all — **compiled out, not greyed
out**, which is the user's explicit choice: an operator who only ever marks
speakers should see the screen they already know.

Keep the `#if !VOXMARK_LITE` surface as small as it is now — three regions in
`SetupWindow`, three in `RecordingWindow`, and the two removed files. Anything
that does not name a Whisper.net type belongs in both editions, so that a
session recorded on a Full machine still opens, exports and reads correctly
in Lite.

`AssemblyName` is `VoxMark` in both, so crash logs, session folders and the
Markdown `tool:` field do not fork; CI renames the Lite output. The running
app names its own edition through `BuildProfile`, on the library screen.

## Settings vs. Setup — what belongs to the PC, what to the meeting

Two homes for what looks like one pile of options, and the line between them
is *what the value is about*, not how often it changes:

- **`SettingsWindow`, reached from the Library** — the machine. Where
  recordings are saved (`AppSettingsStore.SessionsRoot`), the mark-start
  offset and the MP3 bitrate a new meeting starts from, the whisper model
  file and its language (`TranscriptionSettingsStore`), and the copyable
  diagnostics Log. An operator sets these once on this PC.
- **`SetupWindow`, before every meeting** — the meeting. Title, date, room,
  the roster and its keys, presets, split interval, overlapping marks, and
  the Live-transcription on/off toggle. Plus the **unskippable input check**
  — device and level meter — which the design guide's section 07 makes a
  ritual, so it never moves to Settings.

Setup shows a read-only echo with a "Settings" link for each value it no
longer owns, so nobody starts a recording without seeing where it will land.
The echo takes its value from `_options`, which is seeded from the app
defaults but **overridden by a saved plan** — a `MeetingPlan` records what it
was saved with, and reopening it must not silently retune it. Coming back
from the dialog adopts the app defaults, because the operator just chose
them there deliberately.

Two path rules fall out of this and are easy to break:

- `AppPaths.EnsureRoot()` creates `Documents\VoxMark\` and
  `AppPaths.EnsureCreated()` creates that **and** the (redirectable)
  sessions folder. Anything writing an app-level file — `plans.json`,
  `presets.json`, `transcription.json` — calls `EnsureRoot`. They were once
  the same call, and a redirected save location meant `Documents\VoxMark\`
  was never created, so the next `plans.json` write threw
  `FileNotFoundException` from a click handler and took the app down.
- Every store write reachable from a click handler is wrapped, and its
  diagnostic goes to `AppPaths.Note` for the Settings Log. An unhandled
  exception on the dispatcher is not an error message, it is a closed app.

## Output contract (read section 10 before touching `MarkdownExporter`)

The Markdown file is written for an LLM to consume, not a human — the
design guide calls it "a contract, not a suggestion." Field order, the
separate `## Gaps` table, and the `HH:MM:SS.mmm` timestamp format (offset
from the start of `audio_file`, not wall-clock) are all load-bearing.
Don't reshape this without re-reading section 10 and confirming — the
guide itself flags the format as *its* proposal, not a settled requirement,
so if it needs to change, that's a real decision, not a refactor.

The transcript is **additive to that contract, never a change to it**. The
segments table and the `## Gaps` table are untouched; recognised speech goes
into a new `## Transcript` section after them, and the two front-matter keys
(`transcription`, `transcript_coverage`) are appended after the section 10
keys rather than woven among them. A session recorded without transcription
still produces byte-for-byte the file it always did apart from the standing
agent brief below — that is the property to check first if you touch
`MarkdownExporter`.

`## Agent Instructions` is the one section every export carries, transcript
or not: the standing brief for the LLM the file is handed to, asking for a
`transcript.md` holding the verbatim per-speaker transcript, an executive
summary, per-speaker key points, and the same summary and key points again
in Korean. It exists so the operator does not retype that prompt after every
meeting. Like the transcript it is **additive** — last in the file, after
the Notes, with only a one-line pointer under the title so it is not missed
— and it is written in English whatever language the meeting was in, because
it addresses the agent rather than the room. Changing what it asks for
changes what every downstream agent produces, so treat its wording as
interface, not prose.

The brief **branches at read time, not at export time**, because the export
cannot know what the agent will be handed — the MP3 does not always travel
with the Markdown. So it names routes and lets the agent pick: audio present
means transcribe it; audio absent with a `## Transcript` section present
means skip the alignment step and write the report straight from that
section, saying in the output that it came from unreviewed recognition;
audio absent with no transcript means say so and stop, because a transcript
built from speaker names and timings alone is invention. `hasTranscript` is
therefore the only thing the exporter decides — it picks which Route B the
file gets — and the split-session rule lives in the write-up step so it
reaches both routes rather than only the transcribing one.

Attribution rule, stated in the output itself: a segment goes to the mark it
overlaps most. Whisper's boundaries follow its own decoding rather than the
speaker changes, so a segment can straddle a handover; splitting it would
need per-word timings this pipeline does not produce reliably, and inventing
a split point would fabricate attribution the operator never made.

## Windows build & packaging

**The deliverable for Windows users is one `.exe` file, self-contained, no
setup.** This is a project requirement, not a default to optimize away — and
it holds for *both* editions.

```powershell
dotnet publish src/MeetingRecorder/MeetingRecorder.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:Edition=Full `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/full

dotnet publish src/MeetingRecorder/MeetingRecorder.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:Edition=Lite `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/lite
```

This is already encoded in `MeetingRecorder.csproj` (`SelfContained`,
`PublishSingleFile`, `RuntimeIdentifiers=win-x64`, and for Full
`IncludeAllContentForSelfExtract` + `EnableCompressionInSingleFile`) and in
the CI workflow — don't remove those properties to "simplify" the project
file. The workflow also asserts that neither publish directory contains
anything but the exe, because a loose DLL beside it is exactly how this
requirement gets broken by accident.

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

CI (`.github/workflows/build-windows-exe.yml`) publishes both exes on every
push to `main` and uploads them as build artifacts; pushing a `v*` tag also
attaches both to a GitHub Release. If you change build flags, make sure that
workflow still produces two working single-file exes — it's the actual proof
the packaging requirement is met, since it can't be verified locally here.

## Repo conventions

- Default branch: `main`. Feature work happens on branches, opened as PRs
  into `main` — see the repo's CI checks before merging.
- Keep commits scoped to one logical change with a clear message.
- Don't commit recordings, session output, or `bin/`/`obj/`/`publish/` —
  see `.gitignore`.
