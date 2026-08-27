using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// The append-only operation log behind section 06's "editing is journalled"
/// and section 11's "mark state is appended to marks.jsonl and fsync'd on
/// every operation". The engine calls this after every mutation, so a crash
/// mid-meeting loses at most the last operation, never the session.
/// </summary>
public interface IMarkJournal
{
    void RecordUpsert(Mark mark);
    void RecordDelete(long markId);
    void RecordOpenState(IReadOnlyList<OpenMark> open);
}
