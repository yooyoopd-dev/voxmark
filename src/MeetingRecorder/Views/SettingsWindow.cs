using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeetingRecorder.Controls;
using MeetingRecorder.Services;
using MeetingRecorder.Theme;

namespace MeetingRecorder.Views;

/// <summary>
/// The settings that belong to this PC rather than to one meeting: where
/// recordings are saved, the defaults a new setup starts from, which speech
/// model to use, and the diagnostics log.
///
/// They used to sit on the setup screen, which put a folder picker and a
/// diagnostics pane in front of an operator whose actual job at that moment
/// is to check the mic and start recording — and made the left pane taller
/// than the window. Setup keeps a read-only echo of each value with a link
/// back here, so nothing is hidden; it is just not in the way.
///
/// Changes apply as they are made. There is no OK/Cancel: every control here
/// is a single value with a visible effect, and a half-applied settings
/// screen is a worse failure than an immediate one.
/// </summary>
public sealed class SettingsWindow : ShellWindow
{
    private readonly TextBlock _sessionsRoot;
    private readonly TextBlock _disk;
    private readonly TextBlock _status;
    private readonly Dropdown _offset;
    private readonly Dropdown _bitrate;
    private readonly TextBox _log;

#if !VOXMARK_LITE
    private readonly TextBlock _modelName;
    private readonly TextBlock _modelStatus;
    private readonly TextBlock _gpuStatus;
    private readonly TextBlock _cudaPath;
    private readonly Dropdown _language;
#endif

    public SettingsWindow() : base("VoxMark — settings", 820, 760)
    {
        MinWidth = 640;
        MinHeight = 520;

        var settings = AppSettingsStore.Load();

        _sessionsRoot = Ui.Mono("—", 12.5, Palette.TextBodyBrush);
        _sessionsRoot.TextTrimming = TextTrimming.CharacterEllipsis;

        _disk = Ui.Mono("—", 12, Palette.TextMutedBrush);
        _status = Ui.Wrap("", 12, Palette.TextMutedBrush);

        _offset = new Dropdown("ChipButton") { MinHeight = 26 };
        _offset.SetItems(new (string, object)[]
        {
            ("−0.0 s", 0.0), ("−0.4 s", 0.4), ("−0.8 s", 0.8), ("−1.2 s", 1.2), ("−1.6 s", 1.6),
        });
        _offset.SelectionChanged += value =>
        {
            if (value is double seconds) Store(s => s.MarkStartOffsetSeconds = seconds);
        };

        _bitrate = new Dropdown("ChipButton") { MinHeight = 26 };
        _bitrate.SetItems(new (string, object)[]
        {
            ("96 kbps", 96), ("128 kbps", 128), ("192 kbps", 192),
        });
        _bitrate.SelectionChanged += value =>
        {
            if (value is int kbps)
            {
                Store(s => s.Mp3BitrateKbps = kbps);
                RefreshDisk();
            }
        };

        _log = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = Ui.MonoFont,
            FontSize = 11.5,
            Foreground = Palette.TextMutedBrush,
            MinHeight = 110,
        };

#if !VOXMARK_LITE
        WhisperRuntime.EnsureModelsFolder();
        var speech = TranscriptionSettingsStore.Load();

        _modelName = Ui.Mono("—", 12.5, Palette.TextBodyBrush);
        _modelName.TextTrimming = TextTrimming.CharacterEllipsis;
        _modelStatus = Ui.Wrap("", 11.5, Palette.TextMutedBrush);
        _gpuStatus = Ui.Wrap("", 11.5, Palette.TextMutedBrush);
        _cudaPath = Ui.Mono("—", 12.5, Palette.TextBodyBrush);
        _cudaPath.TextTrimming = TextTrimming.CharacterEllipsis;

        _language = new Dropdown("ChipButton") { MinHeight = 26, PopupMinWidth = 160 };
        _language.SetItems(new (string, object)[]
        {
            ("Auto-detect", "auto"), ("English", "en"), ("Korean", "ko"), ("Japanese", "ja"),
            ("Chinese", "zh"), ("Spanish", "es"), ("French", "fr"), ("German", "de"),
        });
        _language.SelectionChanged += value =>
        {
            if (value is string code) Speech(s => s.Language = code);
        };
        _language.Select(speech.Language);
        _language.DisplayText = LanguageLabel(speech.Language);
#endif

        _offset.Select(settings.MarkStartOffsetSeconds);
        _offset.DisplayText = "−" + settings.MarkStartOffsetSeconds.ToString("0.0") + " s";
        _bitrate.Select(settings.Mp3BitrateKbps);
        _bitrate.DisplayText = settings.Mp3BitrateKbps + " kbps";

        SetBody(BuildBody());

        RefreshSaveLocation(probe: true);
        RefreshLog();
#if !VOXMARK_LITE
        RefreshModel();
#endif

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    // ----------------------------------------------------------------- layout

    private UIElement BuildBody()
    {
        // The padding lives on a Border inside the ScrollViewer rather than on
        // the ScrollViewer itself: ScrollViewer.Padding is not part of the
        // scroll extent, so the bottom of it is unreachable once the content
        // overflows — which is exactly how the Log ended up clipped on Setup.
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(22), Child = BuildSections() },
        };

        var close = Ui.MakeButton("Close", "Esc", "AccentButton", (_, _) => Close());
        close.MinHeight = 40;

        var footerRow = Ui.Columns(0, _status, close);
        footerRow.Margin = new Thickness(22, 14, 22, 14);

        var footer = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xE9, 0xE9, 0xED)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Palette.ChromeBrush,
            Child = footerRow,
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(scroller);
        return root;
    }

    private UIElement BuildSections()
    {
        var sections = new List<UIElement>
        {
            Heading("Save recordings to"),
            SaveLocationCard(),
            Heading("Recording defaults"),
            DefaultsCard(),
        };

#if !VOXMARK_LITE
        sections.Add(Heading("Speech recognition"));
        sections.Add(SpeechCard());
#endif

        sections.Add(Heading("Log"));
        sections.Add(LogCard());
        sections.Add(ResetCard());

        return Ui.Vertical(0, sections.ToArray());
    }

    private UIElement SaveLocationCard()
    {
        var browse = Ui.MakeButton("Browse…", null, "ChipButton", (_, _) => BrowseForSaveFolder());
        browse.Margin = new Thickness(10, 0, 0, 0);
        browse.VerticalAlignment = VerticalAlignment.Center;

        var reset = Ui.MakeButton("Reset", null, "LinkButton", (_, _) => ResetSaveFolder());
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.VerticalAlignment = VerticalAlignment.Center;

        var pathRow = Ui.Columns(0, _sessionsRoot, browse, reset);

        var note = Ui.Wrap(
            "Sessions already saved under a previous location won't move or show up in the " +
            "Library after switching — this redirects new meetings, it doesn't migrate old ones. " +
            "Reset goes back to Documents\\VoxMark\\Sessions.",
            11.5, Palette.TextMutedBrush);

        return Card(Ui.Vertical(8, pathRow, _disk, note));
    }

    private UIElement DefaultsCard()
    {
        var offsetRow = Ui.Columns(1,
            Ui.Text("Mark start offset", 13.5, Palette.TextBrush),
            Ui.Filler(),
            _offset);

        var bitrateRow = Ui.Columns(1,
            Ui.Text("MP3 bitrate", 13.5, Palette.TextBrush),
            Ui.Filler(),
            _bitrate);

        var note = Ui.Wrap(
            "A human presses the key after the speaker has already begun, so every mark start is " +
            "shifted back by the offset automatically. Both values seed a new meeting — a setup you " +
            "saved earlier keeps the values it was saved with, and a recording in progress is never " +
            "affected.",
            11.5, Palette.TextMutedBrush);
        note.Margin = new Thickness(0, 10, 0, 0);

        return Card(Ui.Vertical(0, Pad(offsetRow), Ui.Rule(), Pad(bitrateRow), note));
    }

#if !VOXMARK_LITE
    private UIElement SpeechCard()
    {
        var browse = Ui.MakeButton("Browse…", null, "ChipButton", (_, _) => BrowseForModel());
        browse.Margin = new Thickness(10, 0, 0, 0);

        var modelRow = Ui.Columns(0,
            Ui.Vertical(2, Ui.Text("Speech model", 13.5, Palette.TextBrush), _modelName),
            browse);

        var languageRow = Ui.Columns(1,
            Ui.Text("Language", 13.5, Palette.TextBrush),
            Ui.Filler(),
            _language);

        var cudaBrowse = Ui.MakeButton("Browse…", null, "ChipButton", (_, _) => BrowseForCudaFolder());
        cudaBrowse.Margin = new Thickness(10, 0, 0, 0);
        cudaBrowse.VerticalAlignment = VerticalAlignment.Center;

        var cudaReset = Ui.MakeButton("Reset", null, "LinkButton", (_, _) => ResetCudaFolder());
        cudaReset.Margin = new Thickness(8, 0, 0, 0);
        cudaReset.VerticalAlignment = VerticalAlignment.Center;

        var cudaRow = Ui.Columns(0,
            Ui.Vertical(2, Ui.Text("CUDA libraries", 13.5, Palette.TextBrush), _cudaPath),
            cudaBrowse,
            cudaReset);

        var note = Ui.Wrap(
            "The model is a file you supply — VoxMark never downloads one and makes no network calls. " +
            "Drop a ggml .bin into " + WhisperRuntime.ModelsFolder + " and it is found automatically. " +
            "Whether a given meeting transcribes is still the toggle on the setup screen. " +
            "The CUDA folder is only needed when NVIDIA's runtime is not installed on this PC: put " +
            "cudart64_12.dll, cublas64_12.dll and cublasLt64_12.dll in it and speech runs on the GPU. " +
            "They come to about 700 MB, so any drive will do — VoxMark only adds the folder to its " +
            "own search path and writes nothing there.",
            11.5, Palette.TextMutedBrush);
        note.Margin = new Thickness(0, 10, 0, 0);

        return Card(Ui.Vertical(0, Pad(modelRow), Pad(_modelStatus, 4), Pad(_gpuStatus, 4),
                                Ui.Rule(), Pad(languageRow), Ui.Rule(), Pad(cudaRow), note));
    }
#endif

    private UIElement LogCard()
    {
        var copy = Ui.MakeButton("Copy", null, "GhostButton", (_, _) =>
        {
            if (_log.Text.Length > 0) Clipboard.SetText(_log.Text);
        });
        copy.VerticalAlignment = VerticalAlignment.Center;

        var header = Ui.Columns(0,
            Ui.Wrap("Diagnostics from a failed folder creation appear here, copyable.",
                    11.5, Palette.TextMutedBrush),
            copy);

        return Card(Ui.Vertical(8, header, Ui.Well(_log, new Thickness(8), 6)));
    }

    private UIElement ResetCard()
    {
        var reset = Ui.MakeButton("Reset app settings", null, "DangerButton", (_, _) => ResetEverything());
        reset.MinHeight = 36;
        reset.HorizontalAlignment = HorizontalAlignment.Left;

        var note = Ui.Wrap(
            "Puts the save location, the offset and the bitrate back to their defaults. Your presets, " +
            "saved setups, recordings and Markdown files are not touched.",
            11.5, Palette.TextMutedBrush);

        var card = Card(Ui.Vertical(10, note, reset));
        card.Margin = new Thickness(0, 20, 0, 0);
        return card;
    }

    private static Border Card(UIElement child)
    {
        var card = Ui.Card(child, new Thickness(14));
        card.Margin = new Thickness(0, 0, 0, 20);
        return card;
    }

    private static FrameworkElement Heading(string text)
    {
        var label = Ui.Section(text);
        label.Margin = new Thickness(0, 0, 0, 10);
        return label;
    }

    private static FrameworkElement Pad(FrameworkElement element, double vertical = 9)
    {
        element.Margin = new Thickness(0, vertical, 0, vertical);
        return element;
    }

    // ------------------------------------------------------------ save location

    /// <summary>
    /// Point new sessions at a folder the operator picks. Write-probed through
    /// the same retrying create an actual session folder would get, so a folder
    /// that turns out to be just as broken as the default is reported here —
    /// with the full diagnostic in the Log — rather than silently accepted and
    /// only discovered at Start.
    /// </summary>
    private void BrowseForSaveFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where VoxMark saves recordings",
        };

        try
        {
            if (Directory.Exists(AppPaths.SessionsRoot)) dialog.InitialDirectory = AppPaths.SessionsRoot;
        }
        catch (Exception)
        {
            // An unreadable current folder is not worth failing the dialog over.
        }

        if (dialog.ShowDialog(this) != true) return;

        var chosen = dialog.FolderName;
        try
        {
            AppPaths.CreateDirectory(chosen);
        }
        catch (Exception ex)
        {
            var hint = AppPaths.OneDriveHint(chosen);
            AppPaths.Note("Could not use \"" + chosen + "\" as the save location.\n" +
                          ex.GetType().Name + ": " + ex.Message +
                          (hint.Length > 0 ? "\n" + hint : ""));
            RefreshLog();
            Say("That folder could not be used — see the Log below.", Palette.RecBrush);
            return;
        }

        Store(s => s.SessionsRoot = chosen);
        RefreshSaveLocation();
        Say("New recordings will be saved to " + chosen, Palette.GoodBrush);
    }

    private void ResetSaveFolder()
    {
        Store(s => s.SessionsRoot = "");
        RefreshSaveLocation(probe: true);
        Say("Back to the default save location.", Palette.GoodBrush);
    }

    /// <summary>
    /// Show the current location and what fits in it. <paramref name="probe"/>
    /// also tries to create it, which is what makes the readout honest about a
    /// path that no longer works — but it is the retrying create, so it is
    /// asked for only where the location may actually have changed, never on
    /// an unrelated redraw.
    /// </summary>
    private void RefreshSaveLocation(bool probe = false)
    {
        var path = AppPaths.SessionsRoot;
        _sessionsRoot.Text = path;
        _sessionsRoot.ToolTip = path;

        if (probe)
        {
            try
            {
                AppPaths.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                AppPaths.Note("The save location \"" + path + "\" could not be created.\n" +
                              ex.GetType().Name + ": " + ex.Message + "\n" + AppPaths.OneDriveHint(path));
                RefreshLog();
                Say("That save location cannot be used — see the Log below.", Palette.RecBrush);
            }
        }

        RefreshDisk();
    }

    private void RefreshDisk() =>
        _disk.Text = DiskInfo.Describe(AppPaths.SessionsRoot, AppSettingsStore.Load().Mp3BitrateKbps);

    // ------------------------------------------------------------------ speech

#if !VOXMARK_LITE
    /// <summary>Pick a model file by hand, for anyone not using the Models folder.</summary>
    private void BrowseForModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a whisper speech model",
            Filter = "Whisper ggml model (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        try
        {
            if (Directory.Exists(WhisperRuntime.ModelsFolder))
            {
                dialog.InitialDirectory = WhisperRuntime.ModelsFolder;
            }
        }
        catch (Exception)
        {
            // An unreadable folder is not worth failing the dialog over.
        }

        if (dialog.ShowDialog(this) != true) return;

        Speech(s => s.ModelPath = dialog.FileName);
        RefreshModel();
    }

    private void RefreshModel()
    {
        var model = WhisperRuntime.ResolveModel(TranscriptionSettingsStore.Load().ModelPath);

        _modelName.Text = model.Path.Length > 0 ? model.Name : "none found";
        _modelName.Foreground = model.IsUsable ? Palette.AccentTextBrush : Palette.TextMutedBrush;

        // LoadedRuntimeLabel is deliberately not shown here: nothing has built
        // a factory yet on this screen, so it would read "not loaded" beside a
        // model that is in fact ready.
        var trouble = model.Problem ?? model.Warning;
        _modelStatus.Text = trouble ??
            "Ready — turn Live transcription on for a meeting and the words are recognised on this PC.";
        _modelStatus.Foreground = trouble is null ? Palette.TextMutedBrush : Palette.WarnBrush;

        // Which engine this PC will use, on the other hand, *is* knowable
        // without a factory: it is a question about files on disk. It belongs
        // here because it is a property of the machine, and because a CPU
        // fallback costs about a five-fold slowdown that the operator can
        // often fix once and never think about again.
        var gpu = WhisperRuntime.InspectGpu();
        var slow = WhisperRuntime.GpuAdvice(gpu);
        if (slow is not null) WhisperRuntime.EnsureCudaFolder();

        _gpuStatus.Text = WhisperRuntime.GpuSummary(gpu);
        _gpuStatus.Foreground = slow is null ? Palette.TextMutedBrush : Palette.WarnBrush;

        _cudaPath.Text = WhisperRuntime.CudaFolder + (WhisperRuntime.CudaFolderIsCustom ? "" : "  (default)");
        _cudaPath.Foreground = Directory.Exists(WhisperRuntime.CudaFolder)
            ? Palette.TextBodyBrush
            : Palette.TextMutedBrush;
    }

    /// <summary>
    /// Point the CUDA libraries somewhere else. Those three files are about
    /// 700 MB, and a machine whose C: drive is tight has every reason to keep
    /// them on another one — the same argument that made the save location
    /// settable, and the same shape of control.
    /// </summary>
    private void BrowseForCudaFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder holding the CUDA 12 libraries",
        };

        try
        {
            if (Directory.Exists(WhisperRuntime.CudaFolder)) dialog.InitialDirectory = WhisperRuntime.CudaFolder;
        }
        catch (Exception)
        {
            // An unreadable current folder is not worth failing the dialog over.
        }

        if (dialog.ShowDialog(this) != true) return;

        Speech(s => s.CudaPath = dialog.FolderName);

        // On the search path immediately, so the status line below reports
        // what the next recording will actually find rather than what the
        // folder held when this window opened.
        WhisperRuntime.UseCudaFolder();
        RefreshModel();

        var ready = WhisperRuntime.InspectGpu().CudaReady;
        Say(ready
            ? "Found the CUDA libraries — speech recognition will use the GPU."
            : "Saved. That folder does not hold the three CUDA libraries yet — see the line above.",
            ready ? Palette.GoodBrush : Palette.WarnBrush);
    }

    private void ResetCudaFolder()
    {
        Speech(s => s.CudaPath = "");
        WhisperRuntime.UseCudaFolder();
        RefreshModel();
        Say("Back to " + WhisperRuntime.DefaultCudaFolder + ".", Palette.GoodBrush);
    }

    /// <summary>
    /// A code this list does not carry is shown as itself rather than as
    /// "English": whisper accepts more languages than the eight offered here,
    /// and a settings file naming one of them is right — mislabelling it would
    /// be the only wrong thing in the exchange.
    /// </summary>
    private static string LanguageLabel(string code) => code switch
    {
        "auto" => "Auto-detect",
        "en" => "English",
        "ko" => "Korean",
        "ja" => "Japanese",
        "zh" => "Chinese",
        "es" => "Spanish",
        "fr" => "French",
        "de" => "German",
        _ => code,
    };

    /// <summary>Read-modify-write, so one field never clears the others.</summary>
    private static void Speech(Action<TranscriptionSettingsStore.Settings> change)
    {
        var settings = TranscriptionSettingsStore.Load();
        change(settings);
        TranscriptionSettingsStore.Save(settings);
    }
#endif

    // ------------------------------------------------------------------ shared

    private void ResetEverything()
    {
        var answer = MessageBox.Show(
            "Put the save location, mark offset and MP3 bitrate back to their defaults?\n\n" +
            "Your presets, saved setups and recordings are not touched.",
            "VoxMark", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        AppSettingsStore.Save(new AppSettingsStore.Settings());

        var defaults = new AppSettingsStore.Settings();
        _offset.Select(defaults.MarkStartOffsetSeconds);
        _offset.DisplayText = "−" + defaults.MarkStartOffsetSeconds.ToString("0.0") + " s";
        _bitrate.Select(defaults.Mp3BitrateKbps);
        _bitrate.DisplayText = defaults.Mp3BitrateKbps + " kbps";

        RefreshSaveLocation(probe: true);
        Say("App settings reset.", Palette.GoodBrush);
    }

    /// <summary>Read-modify-write, so one field never clears the others.</summary>
    private void Store(Action<AppSettingsStore.Settings> change)
    {
        var settings = AppSettingsStore.Load();
        change(settings);
        AppSettingsStore.Save(settings);
    }

    private void RefreshLog()
    {
        _log.Text = AppPaths.Diagnostics.Count == 0
            ? "No issues yet"
            : string.Join("\n\n", AppPaths.Diagnostics);
        _log.CaretIndex = _log.Text.Length;
        _log.ScrollToEnd();
    }

    private void Say(string message, Brush colour)
    {
        _status.Text = message;
        _status.Foreground = colour;
    }
}
