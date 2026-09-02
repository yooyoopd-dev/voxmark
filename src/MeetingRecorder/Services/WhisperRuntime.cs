using System.IO;
using Whisper.net.LibraryLoader;

namespace MeetingRecorder.Services;

/// <summary>
/// Finds the two things speech recognition needs before it can start: the
/// whisper.cpp native libraries, and a model file. Full edition only — this
/// is one of the two files that names a Whisper.net type, and the Lite build
/// removes it from compilation entirely.
///
/// Both lookups report failure as a message rather than an exception, because
/// of the rule the whole feature lives under (design guide section 11):
/// recording never stops for any reason. A missing model is a sentence in the
/// UI, never a dialog and never a throw on the capture path.
///
/// Nothing here reaches the network. There is no download, no cache fetch and
/// no "first run" step — the operator supplies the model file, which is what
/// keeps the app's offline-only promise literally true on a machine behind a
/// filtering proxy.
/// </summary>
public static class WhisperRuntime
{
    /// <summary>Where an operator can drop model files for the app to find on its own.</summary>
    public static string ModelsFolder => Path.Combine(AppPaths.Root, "Models");

    /// <summary>
    /// An escape hatch: unpack the native runtime here and it wins over
    /// whatever the exe carries. Exists because single-file extraction is the
    /// one part of this that depends on host behaviour rather than on code.
    /// </summary>
    public static string RuntimeOverrideFolder => Path.Combine(AppPaths.Root, "whisper-runtime");

    /// <summary>
    /// Create the models folder if it is missing, so the "put a .bin here"
    /// message names somewhere the operator can actually open. Full edition
    /// only — a Lite install has no use for the folder and should not grow
    /// one.
    /// </summary>
    public static void EnsureModelsFolder()
    {
        try
        {
            Directory.CreateDirectory(ModelsFolder);
        }
        catch (Exception)
        {
            // The message still names the path; it just may not exist yet.
        }
    }

    private static bool _probed;
    private static string? _probeFailure;

    /// <summary>Where the natives were found, once <see cref="Probe"/> has run.</summary>
    public static string? RuntimeRoot { get; private set; }

    /// <summary>
    /// Point Whisper.net's loader at the native libraries.
    ///
    /// The loader does not P/Invoke by name — it walks candidate directories
    /// looking for "&lt;root&gt;/runtimes/win-x64/whisper.dll" and loads it by
    /// path. In a single-file exe those files live wherever the host
    /// extracted them, which is not the directory the exe sits in, so the
    /// search has to be done here and the result handed over through
    /// <see cref="RuntimeOptions.LibraryPath"/>.
    ///
    /// Returns null on success, or a sentence explaining what is missing.
    /// Safe to call repeatedly; only the first call does any work.
    /// </summary>
    public static string? Probe()
    {
        // Ahead of the once-only work, and on every call: a machine with no
        // CUDA toolkit can still run on the GPU if the operator keeps the
        // libraries in a folder of their own, that folder has to be on the
        // search path before whisper.cpp tries its CUDA backend, and it can
        // be chosen in Settings long after the first probe.
        UseCudaFolder();

        if (_probed) return _probeFailure;
        _probed = true;

        try
        {
            foreach (var root in CandidateRoots())
            {
                // Either layout is enough to run: the CPU package lays its
                // libraries out under runtimes/win-x64, the CUDA one under
                // runtimes/cuda/win-x64. A Full build ships both, but an
                // override folder may hold only one.
                if (!File.Exists(Path.Combine(root, "runtimes", "win-x64", "whisper.dll")) &&
                    !File.Exists(Path.Combine(root, "runtimes", "cuda", "win-x64", "whisper.dll")))
                {
                    continue;
                }

                RuntimeRoot = root;
                // The loader takes the *directory* of this path and searches
                // "<that>/runtimes/..." under it, so the file name is a
                // placeholder and only the folder matters.
                RuntimeOptions.LibraryPath = Path.Combine(root, "whisper.dll");
                return _probeFailure = null;
            }

            _probeFailure =
                "The speech recognition engine could not be found inside this build. " +
                "Unpack the whisper runtime into " + RuntimeOverrideFolder + " to fix it, " +
                "or use a build of VoxMark that includes it.";
        }
        catch (Exception ex)
        {
            _probeFailure = "The speech recognition engine could not be loaded: " + ex.Message;
        }

        return _probeFailure;
    }

    /// <summary>
    /// Directories that might hold a "runtimes" tree, best guess first.
    ///
    /// NATIVE_DLL_SEARCH_DIRECTORIES is the one that actually matters for a
    /// published exe: the single-file host sets it to the directory it
    /// extracted the bundle into, which is the only place those DLLs exist at
    /// run time. The rest cover an ordinary framework-dependent build, a
    /// side-by-side layout, and the manual override.
    /// </summary>
    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Enumerate())
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (seen.Add(candidate)) yield return candidate;
        }

        static IEnumerable<string?> Enumerate()
        {
            yield return SafeOverrideFolder();
            yield return AppContext.BaseDirectory;

            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string directories)
            {
                foreach (var directory in directories.Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return directory;
                }
            }

            yield return SafeDirectory(Environment.ProcessPath);
            yield return SafeDirectory(typeof(WhisperRuntime).Assembly.Location);
        }
    }

    private static string? SafeOverrideFolder()
    {
        try
        {
            return Directory.Exists(RuntimeOverrideFolder) ? RuntimeOverrideFolder : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Assembly.Location is an empty string in a single-file app, which Path rejects.</summary>
    private static string? SafeDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What <see cref="ResolveModel"/> came back with. A <c>Problem</c> means
    /// the model cannot be used; a <c>Warning</c> means it looks odd but is
    /// worth trying anyway.
    /// </summary>
    public readonly record struct ModelResult(string Path, string Name, string? Problem, string? Warning = null)
    {
        public bool IsUsable => Problem is null && Path.Length > 0;
    }

    /// <summary>
    /// Settle on a model file. An explicit path wins; otherwise the newest
    /// <c>*.bin</c> in <see cref="ModelsFolder"/>, so dropping a file in that
    /// folder is all the setup there is.
    /// </summary>
    public static ModelResult ResolveModel(string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath)) return Inspect(preferredPath.Trim());

        try
        {
            if (Directory.Exists(ModelsFolder))
            {
                var newest = new DirectoryInfo(ModelsFolder)
                    .EnumerateFiles("*.bin")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest is not null) return Inspect(newest.FullName);
            }
        }
        catch (Exception)
        {
            // Fall through to the "no model" message, which names the folder.
        }

        return new ModelResult("", "", 
            "No speech model found. Put a whisper ggml model (a .bin file, e.g. ggml-small.en.bin) " +
            "in " + ModelsFolder + ", or pick one with Browse. VoxMark never downloads it for you.");
    }

    private static ModelResult Inspect(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new ModelResult(path, Path.GetFileName(path), "That model file no longer exists.");

            // The smallest real whisper model is ~75 MB; anything this size
            // is a stub, a download that stopped, or the wrong file.
            if (info.Length < 1024 * 1024)
            {
                return new ModelResult(path, info.Name,
                    "That file is only " + (info.Length / 1024) + " KB — too small to be a whisper model. " +
                    "It may be an incomplete download.");
            }

            // A warning, not a refusal: the magic identifies today's ggml
            // container, and refusing on it would lock out a future one that
            // whisper.cpp itself would happily load. If it really is the
            // wrong file, the load below fails with whisper's own message.
            var warning = HasGgmlMagic(path)
                ? null
                : "This does not look like a ggml model file — VoxMark will try it anyway.";

            return new ModelResult(path, info.Name, null, warning);
        }
        catch (Exception ex)
        {
            return new ModelResult(path, Path.GetFileName(path), "That model file could not be read: " + ex.Message);
        }
    }

    /// <summary>True when the file starts with the ggml container magic.</summary>
    public static bool HasGgmlMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (stream.Read(magic) < 4) return false;
            // "ggml" either way round — the container has been written with
            // both byte orders over its life.
            return (magic[0] == 'g' && magic[1] == 'g' && magic[2] == 'm' && magic[3] == 'l')
                || (magic[0] == 'l' && magic[1] == 'm' && magic[2] == 'g' && magic[3] == 'g');
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------- the GPU

    /// <summary>
    /// Where the three CUDA libraries live when they did not come from an
    /// installer — for a machine where running NVIDIA's installer is not an
    /// option, which is the target machine here, behind a filtering proxy.
    /// The folder's contents are put on the process's DLL search path before
    /// whisper is loaded.
    ///
    /// Settable in Settings, and read fresh every time rather than cached, so
    /// a change takes effect on the next probe with no extra wiring — the
    /// same arrangement as <see cref="AppPaths.SessionsRoot"/>. The default is
    /// under Documents, but those files are about 700 MB together, so a PC
    /// with a tight C: drive can put them on another drive entirely.
    /// </summary>
    public static string CudaFolder
    {
        get
        {
            var custom = TranscriptionSettingsStore.Load().CudaPath;
            return string.IsNullOrWhiteSpace(custom) ? DefaultCudaFolder : custom.Trim();
        }
    }

    /// <summary>Where the libraries are looked for when Settings names nowhere else.</summary>
    public static string DefaultCudaFolder => Path.Combine(AppPaths.Root, "cuda");

    /// <summary>True when the folder in use is the operator's own choice, not the default.</summary>
    public static bool CudaFolderIsCustom =>
        !string.Equals(CudaFolder, DefaultCudaFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What ggml's CUDA backend imports and what this build deliberately does
    /// not carry. Together they are roughly 700 MB — bundling them would
    /// quadruple an exe whose whole point is being one small file — so they
    /// come from the machine, and when they are absent the backend cannot
    /// load and whisper.cpp runs on the CPU instead.
    /// </summary>
    private static readonly string[] CudaLibraries =
    {
        "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll",
    };

    /// <summary>Whether this machine will actually decode on the GPU, and if not, why not.</summary>
    public readonly record struct GpuStatus(bool HasNvidiaDriver, bool HasCudaBackend,
                                            IReadOnlyList<string> Missing, string? LibrariesFoundIn)
    {
        /// <summary>Everything is in place; the CUDA backend should load.</summary>
        public bool CudaReady => HasNvidiaDriver && HasCudaBackend && Missing.Count == 0;

        /// <summary>An NVIDIA card and a CUDA build, held back only by the missing libraries.</summary>
        public bool CudaFixable => HasNvidiaDriver && HasCudaBackend && Missing.Count > 0;
    }

    /// <summary>
    /// Work out where speech recognition is going to run, without loading
    /// anything. This is what lets the setup screen say "this PC will
    /// transcribe on the CPU" *before* the meeting, which is the only point
    /// at which the operator can still do something about it — by the time a
    /// factory has been built and could be asked directly, the recording has
    /// started.
    /// </summary>
    public static GpuStatus InspectGpu()
    {
        try
        {
            Probe();

            // nvcuda.dll is the driver's own CUDA library; it is there iff an
            // NVIDIA display driver is installed. It is not one of the three
            // below — the driver never installs those.
            var driver = File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));

            var backend = CudaBackendFolder();
            if (backend is null) return new GpuStatus(driver, false, Array.Empty<string>(), null);

            string? foundIn = null;
            var missing = new List<string>();
            foreach (var library in CudaLibraries)
            {
                var found = LocateLibrary(library, backend);
                if (found is null) missing.Add(library);
                else foundIn ??= found;
            }

            return new GpuStatus(driver, true, missing, foundIn);
        }
        catch (Exception)
        {
            // A diagnosis that throws is worth less than no diagnosis: this
            // only ever decides what a status line says.
            return new GpuStatus(false, false, Array.Empty<string>(), null);
        }
    }

    /// <summary>The sentence to show an operator, or null when nothing needs saying.</summary>
    public static string? GpuAdvice(GpuStatus status)
    {
        if (!status.CudaFixable) return null;

        return "Speech recognition will run on the CPU on this PC — roughly five times slower than " +
               "the GPU, so the live transcript lags further behind the room. The NVIDIA card is " +
               "there and this build carries the CUDA engine, but the CUDA 12 libraries it needs " +
               "are not on this machine (" + string.Join(", ", status.Missing) + "). Install the " +
               "NVIDIA CUDA 12 runtime, or put those files in " + CudaFolder +
               " — Settings can point that somewhere else if this drive is short of room.";
    }

    /// <summary>
    /// The same news in one line, for the setup screen — which has to fit a
    /// roster, a device check and a level meter, and where the fix is a
    /// click away under Settings anyway.
    /// </summary>
    public static string? GpuHint(GpuStatus status) => status.CudaFixable
        ? "This PC will transcribe on the CPU — around five times slower than the GPU, so the live " +
          "transcript falls further behind. Settings ▸ Speech recognition says how to change that."
        : null;

    /// <summary>
    /// The same picture as <see cref="GpuAdvice"/> but stated for every case,
    /// including the good ones — what the settings screen shows, where the
    /// operator came to find out how their machine is set up rather than to
    /// be warned about it.
    /// </summary>
    public static string GpuSummary(GpuStatus status)
    {
        if (GpuAdvice(status) is { } advice) return advice;

        if (!status.HasNvidiaDriver)
        {
            return "No NVIDIA GPU on this PC, so speech is recognised on the CPU. That works — it is " +
                   "simply slower, so the live transcript runs further behind the room on a long meeting.";
        }

        if (!status.HasCudaBackend)
        {
            return "This build has no CUDA engine, so speech is recognised on the CPU.";
        }

        return "GPU ready — the CUDA libraries were found in " + (status.LibrariesFoundIn ?? "the search path") +
               ", so speech is recognised on the NVIDIA GPU.";
    }

    /// <summary>
    /// Create the drop-in folder, so a message telling the operator to put
    /// files in it names somewhere that exists. Called only when there is
    /// something to put there — an empty folder on a machine that needs
    /// nothing is just clutter in Documents.
    /// </summary>
    public static void EnsureCudaFolder()
    {
        try
        {
            // Only ever the default. A folder the operator picked already
            // exists — they picked it — and creating a path from a settings
            // file on a drive that may not be plugged in is not this
            // method's business.
            if (CudaFolderIsCustom) return;

            AppPaths.EnsureRoot();
            Directory.CreateDirectory(DefaultCudaFolder);
        }
        catch (Exception)
        {
            // The message still names the path; it just may not exist yet.
        }
    }

    /// <summary>The CUDA runtime folder inside whichever root <see cref="Probe"/> settled on.</summary>
    private static string? CudaBackendFolder()
    {
        if (RuntimeRoot is null) return null;
        var folder = Path.Combine(RuntimeRoot, "runtimes", "cuda", "win-x64");
        return File.Exists(Path.Combine(folder, "whisper.dll")) ? folder : null;
    }

    /// <summary>
    /// Where Windows would find one of the CUDA libraries: beside the backend
    /// itself, in the drop-in folder, in a toolkit install, or on PATH. Same
    /// order the loader uses once <see cref="UseCudaFolder"/> has
    /// run, so a "found" here means the load will find it too.
    /// </summary>
    private static string? LocateLibrary(string fileName, string backendFolder)
    {
        foreach (var folder in SearchFolders(backendFolder))
        {
            try
            {
                if (folder.Length > 0 && File.Exists(Path.Combine(folder, fileName))) return folder;
            }
            catch (Exception)
            {
                // A malformed PATH entry is not worth failing a diagnosis over.
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchFolders(string backendFolder)
    {
        yield return backendFolder;
        yield return CudaFolder;

        var toolkit = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(toolkit)) yield return Path.Combine(toolkit, "bin");

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry.Trim().Trim('"');
        }
    }

    /// <summary>
    /// Put <see cref="CudaFolder"/> on the process's DLL search path, so
    /// libraries kept there are found when ggml's CUDA backend is loaded.
    /// Prepending to PATH rather than calling AddDllDirectory: whisper.cpp's
    /// backend is loaded by absolute path, and mixing the two Win32 search
    /// mechanisms is exactly the kind of subtlety that works on one machine
    /// and not the next. This is process-local — nothing outside VoxMark sees
    /// it, and nothing is written to the machine.
    ///
    /// Safe to call as often as you like, and worth calling whenever the
    /// folder might have changed: it does nothing when the folder is already
    /// on the path, and a folder chosen in Settings after the first probe
    /// would otherwise never be searched.
    /// </summary>
    public static void UseCudaFolder()
    {
        try
        {
            var folder = CudaFolder;
            if (!Directory.Exists(folder)) return;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (path.Contains(folder, StringComparison.OrdinalIgnoreCase)) return;

            Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + path);
        }
        catch (Exception)
        {
            // Without this the CUDA backend simply does not load and the CPU
            // one does — which is the behaviour we already have.
        }
    }

    /// <summary>
    /// Which native runtime actually got loaded, for the status lines and the
    /// Markdown. Only meaningful once a factory has been built — before that
    /// the loader has not run and there is nothing to report.
    /// </summary>
    public static string LoadedRuntimeLabel => RuntimeOptions.LoadedLibrary switch
    {
        null => "Not loaded",
        RuntimeLibrary.Cuda => "CUDA",
        RuntimeLibrary.Vulkan => "Vulkan",
        RuntimeLibrary.OpenVino => "OpenVINO",
        RuntimeLibrary.CoreML => "CoreML",
        RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => "CPU",
        // A backend a later Whisper.net added — name it rather than claiming
        // nothing loaded, which would be the wrong thing to tell an operator
        // whose transcription is in fact running.
        // .Value is safe: the null arm above already matched. Without it the
        // compiler sees object.ToString()'s nullable return and warns.
        var other => other.Value.ToString(),
    };

    /// <summary>"ggml-small.en.bin" reads better in a header as "small.en".</summary>
    public static string ShortModelName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
    }
}
