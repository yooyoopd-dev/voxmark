# VoxMark

VoxMark captures live meetings with precision speaker tagging, giving you
audio and timestamps for your LLM.

One Windows PC sits in the meeting room, records the whole thing to a single
MP3, and lets one operator mark who is speaking by clicking a tile or
pressing a number key. On Stop it writes that MP3 plus a Markdown file of
speaker segments and timestamp gaps — hand both to an LLM and you get a
per-speaker transcript.

It can also do the transcribing itself: turn on **live transcription** and
VoxMark recognises the speech on your own PC while you mark, then writes the
words into the Markdown under the speaker you marked.

Offline only — no accounts, no network calls, no cloud sync. That includes
speech recognition: the model is a file you supply, and VoxMark never
downloads anything.

## Download

**[⬇ Download the latest release](https://github.com/yooyoopd-dev/voxmark/releases/latest)**

There are two files in the Assets list. Both are the same app; pick one.

| | Size | Speech recognition |
|---|---|---|
| **`VoxMark-Lite.exe`** | 157 MB | No — records and marks, exactly as before |
| **`VoxMark.exe`** | 229 MB | Yes — whisper on your GPU or CPU |

**Take Lite unless you want transcription.** Everything else about the two is
identical, and a session recorded with one opens in the other.

Both are large because each one carries the entire .NET runtime and WPF — that
is the price of an exe that needs no install. The full build adds the whisper
engine and its CUDA kernels on top, but is compressed, which is why the gap
between them is smaller than the payload it carries.

No GitHub account, no login, no sign-up — release files are public, so you
can download them on any PC.

> **Note:** the per-build files under the repository's *Actions* tab are
> GitHub *artifacts*, and those always require a GitHub login to download,
> even on a public repository. That's a GitHub restriction, not a setting
> this project can change. Use the Releases page above instead.

## Install and run

There is no installer. `VoxMark.exe` **is** the whole app:

1. Copy `VoxMark.exe` anywhere you like — Desktop, `C:\Tools\`, a USB stick.
2. Double-click it.
3. Windows SmartScreen will warn you the first time, because the file is not
   code-signed. Click **More info → Run anyway**.

That's it. Nothing is written to `Program Files`, nothing is added to the
registry, and there is no uninstaller — to remove VoxMark, delete the file.

**Requirements:** 64-bit Windows 10 or 11. Nothing else. You do **not** need
to install the .NET runtime, ffmpeg, or any codec — the .NET runtime and the
MP3 encoder are embedded inside the exe, which is why it is a fairly large
download. The full build is compressed, so its first launch takes a few
seconds longer while it unpacks; after that it starts normally.

For GPU-accelerated transcription you need an NVIDIA card and a reasonably
current NVIDIA driver — no CUDA Toolkit install, the kernels are inside the
exe. Without one it falls back to the CPU on its own.

## Live transcription

Only in `VoxMark.exe` (the full build), and off until you turn it on.

**1. Get a model.** VoxMark does not download one — that is what keeps the
offline promise true on a locked-down network. You need a whisper *ggml*
model file, which is a single `.bin`. The usual source is Hugging Face's
[`ggerganov/whisper.cpp`](https://huggingface.co/ggerganov/whisper.cpp/tree/main)
repository. On a machine with no access, download it somewhere that has
access and copy the file across.

| Model | File | Size | Good for |
|---|---|---|---|
| `base.en` | `ggml-base.en.bin` | ~150 MB | Any laptop, CPU is fine |
| `small.en` | `ggml-small.en.bin` | ~490 MB | **Start here** on a GPU |
| `medium.en` | `ggml-medium.en.bin` | ~1.5 GB | Best accuracy, needs the GPU |

The `.en` models are English-only and noticeably better at English than the
multilingual ones of the same size.

**2. Drop it in.** Put the `.bin` in `Documents\VoxMark\Models\` and VoxMark
finds it by itself. (Or use **Browse** on the setup screen to point at it
anywhere.)

**3. Turn it on.** Setup → Recording options → **Live transcription**. The
line underneath tells you which model it found and whether it is running on
`CUDA` or `CPU`. If something is missing it says what, and recording still
goes ahead without a transcript — transcription can never stop a recording.

While recording, a three-line strip under the minimap shows the words as they
are recognised, a few seconds behind the room, with each line's timecode in
the colour of whoever was marked at that moment. Scroll it back to re-read;
it stops following the live edge until you scroll to the bottom again.

## Where your recordings go

Everything stays on the machine that recorded it, under your Documents
folder:

```
Documents\VoxMark\
  presets.json                          ← saved rosters
  plans.json                            ← meetings set up in advance
  Sessions\
    2026-03-12_weekly-product-review\
      weekly-product-review_2026-03-12.mp3   ← the audio
      weekly-product-review_2026-03-12.md    ← speaker segments + gaps, for the LLM
      session.json                           ← app state
      marks.jsonl                            ← append-only journal (crash recovery)
      transcript.jsonl                       ← recognised speech (only if transcription was on)
  Models\
    ggml-small.en.bin                     ← speech models you supply (full build only)
```

The `.mp3` and the `.md` are the two files you hand to an LLM. Deleting a
session inside the app only unlists it — VoxMark never deletes your audio.

## Using it

1. **Library** — every past meeting is a folder on this machine. Start a new
   one, or finish exporting a session the app never got to close.
2. **Setup** — title the meeting, pick the input device and watch the level
   meter (it defaults to whatever Windows itself calls the default input, and
   says plainly whether a quiet meter means silence or a device that is not
   sending anything), build the roster. Each speaker gets a marking key (1-9, 0, then
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
   | `←` `→` (`Shift` for 0.5 s) | trim the open mark's start by 0.1 s |
   | `Esc` | stop — asks once, inline |

   The arrow keys are the quick fix for pressing a beat late: they move the
   open mark's start, and the speaker tile and the waveform flag both show
   that time as it moves. The handover boundary travels with it, so a nudge
   never opens a gap. (Once a row is selected in the expanded dock, the
   arrows edit that row instead.)

   The dock flags its own suspects: marks under 2 seconds, and sub-0.3-second
   gaps between two different speakers. Recording never stops while you edit.
4. **Stop** — writes the MP3 and the Markdown into the session folder shown
   above. With transcription on, the Markdown gains a `## Transcript` section
   after the tables, with the recognised words already grouped under the
   speaker you marked:

   ```markdown
   ### 7 · S2 · Jane Park — 00:12:04.100 → 00:12:41.800
   The words spoken during that mark, as one paragraph.

   ### — · unmarked — 00:12:41.800 → 00:12:50.000
   Anything said while no speaker was marked shows up here instead.
   ```

   The existing tables are untouched, so the file still reads exactly as it
   did for anything that already consumed it.

### Setting a meeting up in advance

The Date & time field is the meeting's own time, not a clock — type when the
meeting is. **Save setup** (Ctrl+S) stores the whole thing (title, time, room,
roster, options), and it appears at the top of the library as *Ready to
record*. Walk into the room, pick it, press Start.

### Splitting a long meeting

Under Recording options, **Split recording** rolls to a new MP3 every 1, 2, 5,
10, 15, 30 or 60 minutes instead of writing one large file — useful when whatever
consumes the recording has an upload or context limit. Each MP3 gets its own
Markdown, and **timestamps keep counting from the first file** rather than
restarting, so a time means the same thing in every chunk. Each file says
where it sits:

```yaml
audio_file: weekly-product-review_2026-03-12_part02.mp3
audio_part: 2 of 4
audio_part_start: 00:15:00.000
timebase: offset from the start of part 1, continuing across every part;
          subtract audio_part_start to seek inside audio_file
```

A turn that runs across a boundary appears in both files, cut at the
boundary, so no speech is lost.

## Build from source

Building requires **Windows** and the
[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — WPF's
reference assemblies for `net8.0-windows` only exist on Windows.

```powershell
# run it directly
dotnet run --project src/MeetingRecorder

# the full exe, with speech recognition
dotnet publish src/MeetingRecorder/MeetingRecorder.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:Edition=Full `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/full

# the lite exe, without it
dotnet publish src/MeetingRecorder/MeetingRecorder.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:Edition=Lite `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/lite
```

Each produces a `VoxMark.exe` — one file, the entire deliverable. `Edition`
defaults to `Full`; the release ships the Lite one renamed to
`VoxMark-Lite.exe`. Lite compiles the transcription code out rather than
disabling it, so its setup screen has no row for it at all.

## Releasing a new version

[`.github/workflows/build-windows-exe.yml`](.github/workflows/build-windows-exe.yml)
builds both exes on every push to `main` and on every pull request, and
checks that each one really is a single file with nothing loose beside it.
Publishing a release attaches `VoxMark.exe` and `VoxMark-Lite.exe` to it,
which is what makes the builds downloadable without a login. There are two
ways to trigger that:

**From the Actions tab** — open *Build Windows EXE*, click **Run workflow**,
and type the version (`v1.1.0`). The workflow creates the tag and the release
itself, so this needs no local git and no push rights.

**By pushing a tag:**

```bash
git tag -a v1.1.0 -m "VoxMark 1.1.0"
git push origin v1.1.0
```

Either way the exe appears under that release's Assets once the run
finishes.

## Design and scope

The full behavioural spec is the design guide this was built from —
[`docs/recorder-design-guide.html`](docs/recorder-design-guide.html), meant
to be read as text rather than opened in a browser. Development conventions
and what's implemented vs. deferred are in [`CLAUDE.md`](CLAUDE.md).

All five screens in the design guide are built — library, setup, record,
marks dock, export — along with the global hotkeys, the crash-recovery
journal, presets and live mark repair. Speech recognition is an addition on
top of the guide rather than something it asked for; it leaves the guide's
output contract intact and adds a `## Transcript` section beside it.

Two things are deliberately left out, and both are judgement calls rather
than gaps: **audition playback** in the marks dock (the guide only wants it
with headphones attached, which cannot be detected reliably, so the button
ships in its documented disabled state) and **bundled Inter / JetBrains
Mono** (shipping font files fights the one-small-exe requirement, so the app
substitutes Segoe UI Variable and Cascadia Mono). See "Scope of this build"
in [`CLAUDE.md`](CLAUDE.md) for the full list and the reasoning.

## For Whisper Model

https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin.
