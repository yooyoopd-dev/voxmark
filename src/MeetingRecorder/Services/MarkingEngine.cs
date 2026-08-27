using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// Implements the tap-toggle-with-auto-continue marking model from the
/// design guide (section 09, "Marking semantics"):
///
///   - Press A → A's mark opens at now - 0.8s.
///   - Press B while A is open → A closes at now, B opens at now.
///   - Press A again while A is open → A closes. Nothing opens.
///   - Press A within 1.2s of A closing → the previous mark is reopened
///     instead of a short new one being created (repairs a double-tap).
///
/// All times passed in are "file time" — elapsed seconds of audio actually
/// written — not wall-clock time, so pausing never desynchronises marks
/// from the file.
///
/// Simplification vs. the design guide: undo here is a single linear stack
/// (no redo yet, no "unlimited depth" guarantee), and overlapping marks
/// (the "Allow overlapping marks" setting) are not implemented — every
/// open always closes whatever was open before it.
/// </summary>
public sealed class MarkingEngine
{
    public const double MarkStartOffsetSeconds = 0.8;
    public const double ReopenWindowSeconds = 1.2;

    private readonly List<Mark> _marks = new();
    private readonly Stack<Mark> _undoStack = new();

    private int? _lastClosedSlot;
    private Mark? _lastClosedMark;
    private double _lastClosedAt;

    public IReadOnlyList<Mark> Marks => _marks;
    public int? ActiveSlot { get; private set; }
    public double ActiveStart { get; private set; }

    /// <summary>Toggle the mark for <paramref name="slot"/> at file time <paramref name="now"/>.</summary>
    public void Toggle(int slot, double now)
    {
        if (ActiveSlot == slot)
        {
            CloseActive(now);
            return;
        }

        if (_lastClosedSlot == slot && _lastClosedMark is { } reopenable &&
            now - _lastClosedAt <= ReopenWindowSeconds)
        {
            _marks.Remove(reopenable);
            ActiveSlot = slot;
            ActiveStart = reopenable.StartSeconds;
            _lastClosedSlot = null;
            _lastClosedMark = null;
            return;
        }

        if (ActiveSlot is not null)
        {
            CloseActive(now);
        }

        ActiveSlot = slot;
        ActiveStart = Math.Max(0, now - MarkStartOffsetSeconds);
    }

    /// <summary>Space: close whatever is open, without opening anything new.</summary>
    public void CloseWithoutOpening(double now) => CloseActive(now);

    /// <summary>Stop: close whatever is still open and flag it as auto-closed.</summary>
    public void AutoCloseAtStop(double now)
    {
        if (ActiveSlot is null) return;
        var mark = CloseActive(now);
        if (mark is not null) mark.AutoClosed = true;
    }

    private Mark? CloseActive(double now)
    {
        if (ActiveSlot is null) return null;

        var mark = new Mark { SpeakerSlot = ActiveSlot.Value, StartSeconds = ActiveStart, EndSeconds = now };
        _marks.Add(mark);
        _undoStack.Push(mark);

        _lastClosedSlot = ActiveSlot;
        _lastClosedMark = mark;
        _lastClosedAt = now;

        ActiveSlot = null;
        return mark;
    }

    /// <summary>
    /// Ctrl+Z: cancel the currently open mark if there is one, otherwise
    /// reopen the most recently closed one.
    /// </summary>
    public void Undo()
    {
        if (ActiveSlot is not null)
        {
            ActiveSlot = null;
            return;
        }

        if (_undoStack.Count == 0) return;
        var mark = _undoStack.Pop();
        if (!_marks.Remove(mark)) return;

        ActiveSlot = mark.SpeakerSlot;
        ActiveStart = mark.StartSeconds;

        if (ReferenceEquals(_lastClosedMark, mark))
        {
            _lastClosedMark = null;
            _lastClosedSlot = null;
        }
    }

    /// <summary>Ranges with no speaker marked, for the Markdown "Gaps" section.</summary>
    public IReadOnlyList<(double Start, double End)> ComputeGaps(double totalSeconds)
    {
        var ordered = _marks.OrderBy(m => m.StartSeconds).ToList();
        var gaps = new List<(double, double)>();
        var cursor = 0.0;

        foreach (var mark in ordered)
        {
            if (mark.StartSeconds > cursor)
            {
                gaps.Add((cursor, mark.StartSeconds));
            }
            cursor = Math.Max(cursor, mark.EndSeconds);
        }

        if (cursor < totalSeconds)
        {
            gaps.Add((cursor, totalSeconds));
        }

        return gaps;
    }
}
