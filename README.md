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

**2-1. Set up CUDA acceleration.** Create a folder (e.g., D:\CUDA or wherever you prefer) and copy `cudart64_12.dll`, `cublas64_12.dll`, and `cublasLt64_12.dll` into it. Then, add that folder path to your system PATH (or place the three .dll files directly in the VoxMark application directory).

**3. Turn it on.** Setup → Recording options → **Live transcription**. The
line underneath tells you which model it found and whether it is running on
`CUDA` or `CPU`. If something is missing it says what, and recording still
goes ahead without a transcript — transcription can never stop a recording.

While recording, a six-line strip under the minimap shows the words as they
are recognised, a few seconds behind the room, with each line's timecode in
the colour of whoever was marked at that moment. Scroll it back to re-read;
it stops following the live edge until you scroll to the bottom again.

**Click a line to correct it.** Whisper is good but not right, and a name or a
piece of jargon it mangles is obvious to you in the room and unrecoverable to
whoever reads the file later. Enter commits, Esc abandons, and the correction
goes into the exported Markdown as well as the on-disk journal.

The minimap under the waveform shows the turn in progress as a growing
coloured block, so the speaker being marked right now is visible there and not
only on the tile.

The strip appearing a few seconds behind is the decoder working, not a
mistiming: each line carries the timecode of the audio it came from, which is
the same clock the marks are on. Recognition also **ends a chunk wherever you
marked a speaker change**, so a sentence spoken across a handover is not
decoded as one block and then handed wholesale to one of the two speakers.

### Running it on the GPU

Speech recognition works on the CPU with no setup at all, but it runs at
roughly the speed of the meeting itself — so on a long meeting the live
transcript settles about twenty seconds behind the room. On an NVIDIA GPU the
same model runs several times faster than real time and the strip stays within
a few seconds. **The words are identical either way; only the delay changes.**

`VoxMark.exe` already carries the CUDA engine (`ggml-cuda-whisper.dll`, about
390 MB of the download). What it does **not** carry are NVIDIA's own CUDA 12
libraries — `cudart64_12.dll`, `cublas64_12.dll` and `cublasLt64_12.dll` —
which come to roughly 700 MB between them and would quadruple a file whose
whole point is being one small exe. Those have to be on the machine, and
**the NVIDIA display driver does not install them**: it provides `nvcuda.dll`
and nothing else. If they are missing the CUDA engine cannot load, VoxMark
falls back to the CPU, and the setup screen says so before you start.

**How to tell which one you are on:** the line under *Live transcription* on
the setup screen, or **Settings → Speech recognition**. During a recording,
the status beside the transcript strip reads `… / CUDA` or `… / CPU`.

**Requirements:** an NVIDIA GPU and driver 527 or newer (run `nvidia-smi` — the
"CUDA Version" it prints is the highest your driver supports, and it needs to
be 12.0 or above). VRAM is rarely the limit: `small.en` needs about 1 GB.

Then pick whichever route your machine allows.

**Route 1 — install the CUDA runtime.** Download the *CUDA Toolkit 12.x*
installer from NVIDIA, choose **Custom**, and untick everything except
**CUDA → Runtime**. That puts the three libraries on the PATH and VoxMark
finds them on the next start. Nothing else about VoxMark changes.

**Route 2 — copy three files (no installer, no admin).** For a machine that
cannot run the NVIDIA installer, copy the three DLLs into:

```
Documents\VoxMark\cuda\
```

VoxMark adds that folder to its own search path at startup — nothing is
written to the system, and no other program is affected. The files live in
`%CUDA_PATH%\bin\` on any machine with the toolkit installed, and are also
in NVIDIA's `nvidia-cuda-runtime-cu12` and `nvidia-cublas-cu12` redistributable
packages. Copy them from a machine that has them the same way you copied the
model file across.

**Anywhere else is fine too.** The three files come to about 700 MB, so if the
C: drive is tight, put them on another drive and point VoxMark at it:
**Settings → Speech recognition → CUDA libraries → Browse…** (`D:\cuda\`, a
network share you have mapped, anywhere). **Reset** goes back to the folder
under Documents. VoxMark only adds whichever folder you name to its own search
path and never writes to it, so the same folder can be shared with other
software.

> **Tip — keeping it off a full C: drive.** A single-file exe unpacks itself
> into `%TEMP%\.net\VoxMark\` on first run, and the full build's CUDA engine
> makes that about 400 MB *per version you have run*. Old folders there are
> never cleaned up automatically; deleting all but the newest is safe — the
> next start simply unpacks again.
>
> To move the unpacking itself off C:, set the environment variable
> `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a folder on another drive before
> launching (`setx DOTNET_BUNDLE_EXTRACT_BASE_DIR D:\voxmark-temp`, then open a
> new session). That is read by the .NET host before VoxMark starts, so it
> cannot be a setting inside the app. Between that, **Save recordings to**, the
> model file and **CUDA libraries**, everything large VoxMark touches can live
> on a different drive.

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
   sending anything), build the roster. Each speaker has a **Title** and a
   **Sub title** — a name and whatever else helps you pick them out mid-meeting
   (role, team, seat) — and a marking key (1-9, 0, then Shift+1/Shift+2) you can
   reassign by clicking its key cell; absent people keep their slot and colour.
   Both lines show on the speaker's tile while recording. Save the roster as a
   preset to reuse it.
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

### Marking on the beat

Every mark can be shifted back automatically, on the theory that you press the
key a moment after the speaker starts. **The default is 0 s** — no shift —
because most operators watch the waveform and stamp the boundary they can see,
and a shifted mark moves the handover back into the previous speaker's words,
which is how their last sentence ends up under the next speaker's name.

If you mark by ear instead, Settings ▸ **Mark start offset** offers −0.4 to
−1.6 s. VoxMark journals the raw key-press time with every mark either way, so
the choice is never destructive. Upgrading does not change a value you already
chose — a saved setting is a preference, not a default.

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

Alongside those per-part files, a split session also writes one
`..._full.md` covering the **whole meeting** — every mark, gap and transcript
line on one continuous timeline, with a list of which MP3 holds which stretch
of audio:

```yaml
audio_file: 4 files — see audio_files
audio_files:
  - file: weekly-product-review_2026-03-12_part01.mp3
    start: 00:00:00.000
    end: 00:15:00.000
  ...
audio_parts: 4 (this file covers all of them)
```

That is the file to hand an LLM: it says the same thing as the four together,
without asking anything to stitch them back into one meeting. The audio is
*not* joined — merging MP3s would mean re-encoding them, and a single large
file is usually the thing the split was avoiding.

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

## For Whisper Model & CUDA 12 Runtime

https://huggingface.co/ggerganov/whisper.cpp/tree/main

Galaxy Book 4 Ultra (CUDA RTX 4050): ggml-large-v3-turbo-q8_0.bin
Galaxy Book (Non-CUDA): ggml-small-q5_1.bin (English only: ggml-small.en-q5_1.bin)

https://developer.nvidia.com/cuda-12-0-0-download-archive
