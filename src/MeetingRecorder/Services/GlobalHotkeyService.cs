using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// Alt+1…0 and Alt+Shift+1/2 while any other app has focus — design guide
/// section 09. Each press raises the mini-bar toast and nothing else; focus
/// is never taken.
///
/// The guide is explicit about the failure mode to avoid: "the app must test
/// each registration at start and mark the ones Windows refused, since a
/// silently unregistered hotkey looks identical to a missed press." So
/// <see cref="Failed"/> is surfaced in the recording header rather than
/// swallowed.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, MarkKey> _registered = new();
    private HwndSource? _source;
    private int _nextId = 0xB000;

    /// <summary>Keys Windows refused to give us, usually because another app holds them.</summary>
    public List<MarkKey> Failed { get; } = new();

    public event Action<MarkKey>? Pressed;

    /// <summary>Hook the window's message loop. Safe to call from <c>SourceInitialized</c> onwards.</summary>
    public void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    public void Register(IEnumerable<MarkKey> keys)
    {
        if (_source is null) return;

        foreach (var key in keys)
        {
            if (!key.IsValid) continue;

            var modifiers = ModAlt | ModNoRepeat | (key.Shift ? ModShift : 0u);
            var virtualKey = (uint)(0x30 + key.Digit);
            var id = _nextId++;

            if (RegisterHotKey(_source.Handle, id, modifiers, virtualKey))
            {
                _registered[id] = key;
            }
            else
            {
                Failed.Add(key);
            }
        }
    }

    /// <summary>Drop every registration, e.g. when the roster's keys are reassigned.</summary>
    public void UnregisterAll()
    {
        if (_source is null) return;
        foreach (var id in _registered.Keys.ToList())
        {
            UnregisterHotKey(_source.Handle, id);
        }
        _registered.Clear();
        Failed.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var key))
        {
            handled = true;
            Pressed?.Invoke(key);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
