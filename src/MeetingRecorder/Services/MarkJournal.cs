using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// <c>marks.jsonl</c> — the append-only operation log. Every write is
/// followed by an fsync, which is what makes section 11's promise real: a
/// crash mid-meeting loses at most the last operation, never the session.
///
/// Lines are deltas, not snapshots, so the file stays small over a long
/// meeting. <see cref="Replay"/> folds them back into a mark list.
/// </summary>
public sealed class MarkJournal : IMarkJournal, IDisposable
{
    private readonly FileStream _stream;
    private readonly object _lock = new();

    public MarkJournal(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public void RecordUpsert(Mark mark)
    {
        var line = new StringBuilder();
        line.Append("{\"op\":\"upsert\",\"id\":").Append(mark.Id)
            .Append(",\"slot\":").Append(mark.SpeakerSlot)
            .Append(",\"start\":").Append(Number(mark.StartSeconds))
            .Append(",\"end\":").Append(Number(mark.EndSeconds))
            .Append(",\"raw\":").Append(Number(mark.RawPressSeconds))
            .Append(",\"auto\":").Append(mark.AutoClosed ? "true" : "false")
            .Append("}");
        Write(line.ToString());
    }

    public void RecordDelete(long markId) =>
        Write("{\"op\":\"delete\",\"id\":" + markId.ToString(CultureInfo.InvariantCulture) + "}");

    public void RecordOpenState(IReadOnlyList<OpenMark> open)
    {
        var line = new StringBuilder("{\"op\":\"open\",\"marks\":[");
        for (var i = 0; i < open.Count; i++)
        {
            if (i > 0) line.Append(',');
            line.Append("{\"slot\":").Append(open[i].SpeakerSlot)
                .Append(",\"start\":").Append(Number(open[i].StartSeconds))
                .Append(",\"raw\":").Append(Number(open[i].RawPressSeconds))
                .Append('}');
        }
        line.Append("]}");
        Write(line.ToString());
    }

    /// <summary>Round-trippable, culture-invariant, and never scientific notation for these ranges.</summary>
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void Write(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        lock (_lock)
        {
            _stream.Write(bytes, 0, bytes.Length);
            // Not Flush(): the guide asks for an fsync per operation, so the
            // OS write-back cache must be pushed to the device as well.
            _stream.Flush(true);
        }
    }

    public void Dispose()
    {
        lock (_lock) _stream.Dispose();
    }

    /// <summary>Rebuild mark state from a journal file. Malformed trailing lines are ignored.</summary>
    public static (List<Mark> Marks, List<OpenMark> Open) Replay(string path)
    {
        var marks = new Dictionary<long, Mark>();
        var open = new List<OpenMark>();
        if (!File.Exists(path)) return (new List<Mark>(), open);

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var op = root.GetProperty("op").GetString();
                if (op == "upsert")
                {
                    var id = root.GetProperty("id").GetInt64();
                    marks[id] = new Mark
                    {
                        Id = id,
                        SpeakerSlot = root.GetProperty("slot").GetInt32(),
                        StartSeconds = root.GetProperty("start").GetDouble(),
                        EndSeconds = root.GetProperty("end").GetDouble(),
                        RawPressSeconds = root.GetProperty("raw").GetDouble(),
                        AutoClosed = root.GetProperty("auto").GetBoolean(),
                    };
                }
                else if (op == "delete")
                {
                    marks.Remove(root.GetProperty("id").GetInt64());
                }
                else if (op == "open")
                {
                    open.Clear();
                    foreach (var entry in root.GetProperty("marks").EnumerateArray())
                    {
                        open.Add(new OpenMark
                        {
                            SpeakerSlot = entry.GetProperty("slot").GetInt32(),
                            StartSeconds = entry.GetProperty("start").GetDouble(),
                            RawPressSeconds = entry.GetProperty("raw").GetDouble(),
                        });
                    }
                }
            }
            catch (Exception)
            {
                // A half-written last line is exactly what a crash leaves
                // behind; everything before it is still good.
            }
        }

        return (marks.Values.OrderBy(m => m.StartSeconds).ToList(), open);
    }
}
