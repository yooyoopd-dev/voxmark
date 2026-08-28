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

    /// <summary>
    /// Which native runtime actually got loaded, for the status lines and the
    /// Markdown. Only meaningful once a factory has been built — before that
    /// the loader has not run and there is nothing to report.
    /// </summary>
    public static string LoadedRuntimeLabel => RuntimeOptions.LoadedLibrary switch
    {
        RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => "CUDA",
        RuntimeLibrary.Vulkan => "Vulkan",
        RuntimeLibrary.OpenVino => "OpenVINO",
        RuntimeLibrary.CoreML => "CoreML",
        RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => "CPU",
        _ => "not loaded",
    };

    /// <summary>"ggml-small.en.bin" reads better in a header as "small.en".</summary>
    public static string ShortModelName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
    }
}
