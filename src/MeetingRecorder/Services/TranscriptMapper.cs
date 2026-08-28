using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// Joins recognised speech to the operator's marks. Pure logic, no UI and no
/// whisper dependency, so it compiles into both editions and can be reasoned
/// about on its own.
///
/// The rule is deliberately simple and stated in the output: a segment is
/// attributed to whichever mark it shares the most time with. Whisper's
/// segment boundaries follow its own decoding, not the speaker changes, so a
/// segment can straddle a handover — splitting it would need per-word
/// timings this pipeline does not produce reliably, and inventing a split
/// point would fabricate attribution the operator never made. Giving the
/// whole segment to the dominant speaker is wrong less often, and wrong
/// visibly rather than silently.
/// </summary>
public static class TranscriptMapper
{
    /// <summary>A run of segments that all belong to the same mark, in order.</summary>
    public sealed class Block
    {
        /// <summary>The mark these words were attributed to; null when none overlapped.</summary>
        public Mark? Mark { get; init; }

        public List<TranscriptSegment> Segments { get; } = new();

        public double StartSeconds => Segments.Count > 0 ? Segments[0].StartSeconds : 0;
        public double EndSeconds => Segments.Count > 0 ? Segments[^1].EndSeconds : 0;

        /// <summary>The words as one paragraph, single-spaced and trimmed.</summary>
        public string Text => string.Join(" ", Segments
            .Select(s => s.Text.Trim())
            .Where(t => t.Length > 0));
    }

    /// <summary>
    /// The mark a segment belongs to, or null. Ties — a segment split evenly
    /// across two marks — go to the earlier one, so the result does not
    /// depend on list order.
    /// </summary>
    public static Mark? MarkFor(TranscriptSegment segment, IReadOnlyList<Mark> marks)
    {
        Mark? best = null;
        var bestOverlap = 0.0;

        foreach (var mark in marks)
        {
            var overlap = segment.OverlapWith(mark.StartSeconds, mark.EndSeconds);
            if (overlap <= bestOverlap) continue;
            bestOverlap = overlap;
            best = mark;
        }

        return bestOverlap > 0 ? best : null;
    }

    /// <summary>
    /// Group a transcript into consecutive same-speaker blocks. Segments with
    /// no text are dropped — whisper emits blank ones for silence, and a row
    /// with no words is noise in a file meant for an LLM to read.
    /// </summary>
    public static List<Block> Blocks(IEnumerable<TranscriptSegment> segments, IReadOnlyList<Mark> marks)
    {
        var blocks = new List<Block>();
        Block? current = null;

        foreach (var segment in segments.OrderBy(s => s.StartSeconds).ThenBy(s => s.EndSeconds))
        {
            if (segment.Text.Trim().Length == 0) continue;

            var mark = MarkFor(segment, marks);
            // Compared by identity, not by id: an unmarked run has no id to
            // compare, and two consecutive unmarked segments belong together.
            if (current is null || !ReferenceEquals(current.Mark, mark))
            {
                current = new Block { Mark = mark };
                blocks.Add(current);
            }

            current.Segments.Add(segment);
        }

        return blocks;
    }

    /// <summary>Seconds of audio the transcript actually covers, for the front matter.</summary>
    public static double Coverage(IEnumerable<TranscriptSegment> segments)
    {
        var total = 0.0;
        var reachedTo = double.NegativeInfinity;

        // Merging as we go, rather than summing durations, so overlapping
        // segments cannot report more coverage than the meeting has seconds.
        foreach (var segment in segments.OrderBy(s => s.StartSeconds))
        {
            var from = Math.Max(segment.StartSeconds, reachedTo);
            if (segment.EndSeconds > from) total += segment.EndSeconds - from;
            if (segment.EndSeconds > reachedTo) reachedTo = segment.EndSeconds;
        }

        return total;
    }

    private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n' };

    /// <summary>Whitespace-separated words, for the export screen's readout.</summary>
    public static int WordCount(IEnumerable<TranscriptSegment> segments) =>
        segments.Sum(s => s.Text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).Length);
}
