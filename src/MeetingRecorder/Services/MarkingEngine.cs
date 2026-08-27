using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>What one key press actually did, so the mini-bar toast can state both halves.</summary>
public sealed record MarkToggleResult(int? OpenedSlot, int? ClosedSlot, double ClosedDuration, double ClosedAt, bool Reopened);

/// <summary>
/// The marking model from design guide section 09, plus the live repair
/// operations from section 06.
///
/// Marking semantics — tap toggle with auto-continue:
///   - Press A → A's mark opens at now − 0.8 s.
///   - Press B while A is open → A closes at now, B opens at now. One
///     boundary, no gap.
///   - Press A again while A is open → A closes. Nothing opens.
///   - Press A within 1.2 s of A closing → the previous mark is reopened
///     rather than a short second one being created, which silently repairs
///     the most common double-tap error.
///   - Overlap off (default): opening B always closes A. Overlap on: both
///     stay open.
///
/// Every time passed in is file time — elapsed seconds of audio actually
/// written — never wall clock, so pausing cannot desynchronise marks from
/// the MP3 (section 11).
///
/// Undo is snapshot based and therefore genuinely unlimited within a session
/// in both directions, as section 09 requires; every mutation also lands in
/// the journal before it is visible.
/// </summary>
public sealed class MarkingEngine
{
    public const double ReopenWindowSeconds = 1.2;

    /// <summary>Section 06: marks under 2 s are flagged as suspects.</summary>
    public const double SuspectDurationSeconds = 2.0;

    /// <summary>Section 06: a gap under 0.3 s between two different speakers is a suspect.</summary>
    public const double SuspectGapSeconds = 0.3;

    public const double NudgeStepSeconds = 0.5;
    public const double FineNudgeStepSeconds = 0.1;

    /// <summary>Nothing may be edited into a sliver shorter than this.</summary>
    private const double MinimumMarkSeconds = 0.05;

    private readonly SessionOptions _options;
    private readonly List<Mark> _marks = new();
    private readonly List<OpenMark> _open = new();
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    private long _nextId = 1;
    private int? _lastClosedSlot;
    private long _lastClosedId;
    private double _lastClosedAt;

    public MarkingEngine(SessionOptions options) => _options = options;

    public IMarkJournal? Journal { get; set; }

    /// <summary>Resolves a slot to a display name, so notices can name the mark they trimmed.</summary>
    public Func<int, string>? SpeakerNameResolver { get; set; }

    /// <summary>Current file position, used as the upper bound for edits.</summary>
    public double CurrentFileSeconds { get; set; }

    /// <summary>One-line messages for the dock's toast strip ("Trimmed Park Seo-yeon's mark").</summary>
    public event Action<string>? Notice;

    /// <summary>Raised after any mutation, so the UI can re-read state.</summary>
    public event Action? Changed;

    public IReadOnlyList<Mark> Marks => _marks;
    public IReadOnlyList<OpenMark> Open => _open;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>The single open slot, for the tile highlight when overlap is off.</summary>
    public int? ActiveSlot => _open.Count > 0 ? _open[^1].SpeakerSlot : null;

    public double ActiveStart => _open.Count > 0 ? _open[^1].StartSeconds : 0;

    public bool IsOpen(int slot) => _open.Any(o => o.SpeakerSlot == slot);

    public double OpenStartFor(int slot) =>
        _open.FirstOrDefault(o => o.SpeakerSlot == slot)?.StartSeconds ?? 0;

    /// <summary>Total marked time per slot, including the still-open mark.</summary>
    public double TalkTimeFor(int slot, double now)
    {
        var total = _marks.Where(m => m.SpeakerSlot == slot).Sum(m => m.DurationSeconds);
        var open = _open.FirstOrDefault(o => o.SpeakerSlot == slot);
        if (open is not null) total += Math.Max(0, now - open.StartSeconds);
        return total;
    }

    // ------------------------------------------------------------------ marking

    /// <summary>Toggle the mark for <paramref name="slot"/> at file time <paramref name="now"/>.</summary>
    public MarkToggleResult Toggle(int slot, double now)
    {
        var before = Capture();

        int? closedSlot = null;
        double closedDuration = 0;
        var reopened = false;

        var already = _open.FirstOrDefault(o => o.SpeakerSlot == slot);
        if (already is not null)
        {
            var mark = Close(already, now);
            Commit(before);
            return new MarkToggleResult(null, slot, mark?.DurationSeconds ?? 0, now, false);
        }

        // Double-tap repair: reopen the mark that just closed instead of
        // stranding a sliver behind and starting a new one.
        if (_lastClosedSlot == slot && now - _lastClosedAt <= ReopenWindowSeconds &&
            _marks.FirstOrDefault(m => m.Id == _lastClosedId) is { } reopenable)
        {
            if (!_options.AllowOverlappingMarks)
            {
                foreach (var other in _open.ToList())
                {
                    var closed = Close(other, now);
                    closedSlot = other.SpeakerSlot;
                    closedDuration = closed?.DurationSeconds ?? 0;
                }
            }

            _marks.Remove(reopenable);
            _open.Add(new OpenMark
            {
                SpeakerSlot = slot,
                StartSeconds = reopenable.StartSeconds,
                RawPressSeconds = reopenable.RawPressSeconds,
            });
            _lastClosedSlot = null;
            reopened = true;
            Commit(before);
            return new MarkToggleResult(slot, closedSlot, closedDuration, now, true);
        }

        if (!_options.AllowOverlappingMarks)
        {
            foreach (var other in _open.ToList())
            {
                var closed = Close(other, now);
                closedSlot = other.SpeakerSlot;
                closedDuration = closed?.DurationSeconds ?? 0;
            }
        }

        _open.Add(new OpenMark
        {
            SpeakerSlot = slot,
            // The operator presses the key after the speaker has begun, so
            // the start is shifted back automatically; the raw press time is
            // kept so the offset can be re-tuned later (section 07).
            StartSeconds = Math.Max(0, now - _options.MarkStartOffsetSeconds),
            RawPressSeconds = now,
        });

        Commit(before);
        return new MarkToggleResult(slot, closedSlot, closedDuration, now, reopened);
    }

    /// <summary>Space: close whatever is open without opening anything (silence / crosstalk).</summary>
    public MarkToggleResult CloseAll(double now)
    {
        if (_open.Count == 0) return new MarkToggleResult(null, null, 0, now, false);

        var before = Capture();
        int? closedSlot = null;
        double closedDuration = 0;
        foreach (var open in _open.ToList())
        {
            var mark = Close(open, now);
            closedSlot = open.SpeakerSlot;
            closedDuration = mark?.DurationSeconds ?? 0;
        }
        Commit(before);
        return new MarkToggleResult(null, closedSlot, closedDuration, now, false);
    }

    /// <summary>Stop and Pause both close any open mark and flag it auto-closed (section 08).</summary>
    public void AutoCloseAt(double now)
    {
        if (_open.Count == 0) return;

        var before = Capture();
        foreach (var open in _open.ToList())
        {
            var mark = Close(open, now);
            if (mark is not null) mark.AutoClosed = true;
        }
        Commit(before);
    }

    private Mark? Close(OpenMark open, double now)
    {
        _open.Remove(open);

        var end = Math.Max(open.StartSeconds + MinimumMarkSeconds, now);
        var mark = new Mark
        {
            Id = _nextId++,
            SpeakerSlot = open.SpeakerSlot,
            StartSeconds = open.StartSeconds,
            EndSeconds = end,
            RawPressSeconds = open.RawPressSeconds,
        };
        _marks.Add(mark);

        _lastClosedSlot = open.SpeakerSlot;
        _lastClosedId = mark.Id;
        _lastClosedAt = now;
        return mark;
    }

    // ------------------------------------------------------------------- repair

    public Mark? ById(long id) => _marks.FirstOrDefault(m => m.Id == id);

    /// <summary>Marks newest first, which is the order the dock lists them in.</summary>
    public IReadOnlyList<Mark> NewestFirst() =>
        _marks.OrderByDescending(m => m.EndSeconds).ThenByDescending(m => m.Id).ToList();

    public IReadOnlyList<Mark> Chronological() =>
        _marks.OrderBy(m => m.StartSeconds).ThenBy(m => m.Id).ToList();

    public bool Delete(long id)
    {
        var mark = ById(id);
        if (mark is null) return false;

        var before = Capture();
        _marks.Remove(mark);
        Commit(before);
        return true;
    }

    public bool Reassign(long id, int slot)
    {
        var mark = ById(id);
        if (mark is null || mark.SpeakerSlot == slot) return false;

        var before = Capture();
        mark.SpeakerSlot = slot;
        Commit(before);
        return true;
    }

    public bool NudgeStart(long id, double delta) =>
        ById(id) is { } m && SetBounds(id, m.StartSeconds + delta, m.EndSeconds);

    public bool NudgeEnd(long id, double delta) =>
        ById(id) is { } m && SetBounds(id, m.StartSeconds, m.EndSeconds + delta);

    /// <summary>
    /// Move a mark's boundaries. Start can never pass end; if the edit
    /// overlaps a neighbour the neighbour is trimmed and a notice says which
    /// one (section 06, "dock rules").
    /// </summary>
    public bool SetBounds(long id, double start, double end)
    {
        var mark = ById(id);
        if (mark is null) return false;

        var limit = Math.Max(CurrentFileSeconds, _marks.Max(m => m.EndSeconds));
        start = Math.Clamp(start, 0, Math.Max(0, limit - MinimumMarkSeconds));
        end = Math.Clamp(end, MinimumMarkSeconds, limit);
        if (end - start < MinimumMarkSeconds) return false;
        if (Math.Abs(start - mark.StartSeconds) < 1e-6 && Math.Abs(end - mark.EndSeconds) < 1e-6) return false;

        var before = Capture();
        mark.StartSeconds = start;
        mark.EndSeconds = end;
        if (!_options.AllowOverlappingMarks) TrimNeighbours(mark);
        Commit(before);
        return true;
    }

    private void TrimNeighbours(Mark mark)
    {
        foreach (var other in _marks.Where(m => m.Id != mark.Id).ToList())
        {
            if (other.EndSeconds <= mark.StartSeconds || other.StartSeconds >= mark.EndSeconds) continue;

            if (other.StartSeconds >= mark.StartSeconds && other.EndSeconds <= mark.EndSeconds)
            {
                _marks.Remove(other);
                Announce("Removed " + NameOf(other.SpeakerSlot) + "'s mark — it was swallowed by this edit");
                continue;
            }

            if (other.StartSeconds < mark.StartSeconds)
            {
                other.EndSeconds = mark.StartSeconds;
                Announce("Trimmed the end of " + NameOf(other.SpeakerSlot) + "'s mark");
            }
            else
            {
                other.StartSeconds = mark.EndSeconds;
                Announce("Trimmed the start of " + NameOf(other.SpeakerSlot) + "'s mark");
            }
        }
    }

    public bool CanSplit(long id, double at) =>
        ById(id) is { } m && at > m.StartSeconds + MinimumMarkSeconds && at < m.EndSeconds - MinimumMarkSeconds;

    public bool Split(long id, double at)
    {
        if (!CanSplit(id, at)) return false;
        var mark = ById(id)!;

        var before = Capture();
        _marks.Add(new Mark
        {
            Id = _nextId++,
            SpeakerSlot = mark.SpeakerSlot,
            StartSeconds = at,
            EndSeconds = mark.EndSeconds,
            RawPressSeconds = at,
        });
        mark.EndSeconds = at;
        Commit(before);
        return true;
    }

    /// <summary>The mark immediately before this one in time, if any.</summary>
    public Mark? PreviousOf(long id)
    {
        var mark = ById(id);
        if (mark is null) return null;
        return _marks
            .Where(m => m.Id != id && m.StartSeconds <= mark.StartSeconds)
            .OrderByDescending(m => m.StartSeconds)
            .FirstOrDefault();
    }

    public bool MergeWithPrevious(long id)
    {
        var mark = ById(id);
        var previous = PreviousOf(id);
        if (mark is null || previous is null) return false;

        var before = Capture();
        mark.StartSeconds = Math.Min(mark.StartSeconds, previous.StartSeconds);
        mark.EndSeconds = Math.Max(mark.EndSeconds, previous.EndSeconds);
        _marks.Remove(previous);
        Announce("Merged with " + NameOf(previous.SpeakerSlot) + "'s previous mark");
        Commit(before);
        return true;
    }

    /// <summary>Whether there is a gap in front of this mark big enough to fill.</summary>
    public bool CanInsertBefore(long id)
    {
        var mark = ById(id);
        if (mark is null) return false;
        var start = PreviousOf(id)?.EndSeconds ?? 0;
        return mark.StartSeconds - start >= MinimumMarkSeconds * 4;
    }

    public long? InsertBefore(long id, int slot)
    {
        if (!CanInsertBefore(id)) return null;
        var mark = ById(id)!;
        var start = PreviousOf(id)?.EndSeconds ?? 0;

        var before = Capture();
        var inserted = new Mark
        {
            Id = _nextId++,
            SpeakerSlot = slot,
            StartSeconds = start,
            EndSeconds = mark.StartSeconds,
            RawPressSeconds = start,
        };
        _marks.Add(inserted);
        Commit(before);
        return inserted.Id;
    }

    // -------------------------------------------------------------- suspects

    public bool IsShort(Mark mark) => mark.DurationSeconds < SuspectDurationSeconds;

    /// <summary>
    /// Section 06: "The app flags its own suspects" — marks under 2 s, and
    /// gaps under 0.3 s between two marks of different speakers. This is
    /// called the single most valuable feature for transcript quality.
    /// </summary>
    public string? SuspectReason(Mark mark)
    {
        if (IsShort(mark))
        {
            return "Suspiciously short — merge or delete?";
        }

        var previous = PreviousOf(mark.Id);
        if (previous is not null && previous.SpeakerSlot != mark.SpeakerSlot)
        {
            var gap = mark.StartSeconds - previous.EndSeconds;
            if (gap > 0 && gap < SuspectGapSeconds)
            {
                return "Only " + gap.ToString("0.00") + " s after " + NameOf(previous.SpeakerSlot) + " — one turn or two?";
            }
        }
        return null;
    }

    // ------------------------------------------------------------ undo / redo

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(Capture());
        Restore(snapshot);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Capture());
        Restore(snapshot);
        return true;
    }

    // ------------------------------------------------------------------ gaps

    /// <summary>Ranges with no speaker marked, for the Markdown "## Gaps" table.</summary>
    public IReadOnlyList<Gap> ComputeGaps(double totalSeconds)
    {
        var gaps = new List<Gap>();
        var cursor = 0.0;

        foreach (var mark in Chronological())
        {
            if (mark.StartSeconds > cursor) gaps.Add(new Gap { Start = cursor, End = mark.StartSeconds });
            cursor = Math.Max(cursor, mark.EndSeconds);
        }

        if (cursor < totalSeconds) gaps.Add(new Gap { Start = cursor, End = totalSeconds });
        return gaps;
    }

    // ------------------------------------------------------------ recovery

    /// <summary>Reload state rebuilt from the journal after a crash.</summary>
    public void LoadRecovered(IEnumerable<Mark> marks, IEnumerable<OpenMark> open)
    {
        _marks.Clear();
        _marks.AddRange(marks.Select(m => m.Clone()));
        _open.Clear();
        _open.AddRange(open.Select(o => o.Clone()));
        _nextId = _marks.Count == 0 ? 1 : _marks.Max(m => m.Id) + 1;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------- internals

    private sealed record Snapshot(List<Mark> Marks, List<OpenMark> Open, long NextId,
                                   int? LastClosedSlot, long LastClosedId, double LastClosedAt);

    private Snapshot Capture() => new(
        _marks.Select(m => m.Clone()).ToList(),
        _open.Select(o => o.Clone()).ToList(),
        _nextId, _lastClosedSlot, _lastClosedId, _lastClosedAt);

    /// <summary>Push the pre-state onto the undo stack, journal the diff, notify.</summary>
    private void Commit(Snapshot before)
    {
        _undo.Add(before);
        _redo.Clear();
        WriteJournal(before.Marks);
        Changed?.Invoke();
    }

    private void Restore(Snapshot snapshot)
    {
        var before = _marks.Select(m => m.Clone()).ToList();

        _marks.Clear();
        _marks.AddRange(snapshot.Marks.Select(m => m.Clone()));
        _open.Clear();
        _open.AddRange(snapshot.Open.Select(o => o.Clone()));
        _nextId = snapshot.NextId;
        _lastClosedSlot = snapshot.LastClosedSlot;
        _lastClosedId = snapshot.LastClosedId;
        _lastClosedAt = snapshot.LastClosedAt;

        WriteJournal(before);
        Changed?.Invoke();
    }

    /// <summary>Append only what actually changed, so the journal stays small but complete.</summary>
    private void WriteJournal(List<Mark> before)
    {
        if (Journal is null) return;

        var previous = before.ToDictionary(m => m.Id);
        foreach (var mark in _marks)
        {
            if (!previous.TryGetValue(mark.Id, out var old) ||
                old.SpeakerSlot != mark.SpeakerSlot ||
                Math.Abs(old.StartSeconds - mark.StartSeconds) > 1e-9 ||
                Math.Abs(old.EndSeconds - mark.EndSeconds) > 1e-9 ||
                old.AutoClosed != mark.AutoClosed)
            {
                Journal.RecordUpsert(mark);
            }
        }

        var current = _marks.Select(m => m.Id).ToHashSet();
        foreach (var mark in before)
        {
            if (!current.Contains(mark.Id)) Journal.RecordDelete(mark.Id);
        }

        Journal.RecordOpenState(_open);
    }

    private string NameOf(int slot) => SpeakerNameResolver?.Invoke(slot) ?? ("slot " + (slot + 1).ToString());

    private void Announce(string message) => Notice?.Invoke(message);
}
