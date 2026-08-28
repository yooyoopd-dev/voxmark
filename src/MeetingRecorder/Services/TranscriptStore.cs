using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using MeetingRecorder.Models;

namespace MeetingRecorder.Services;

/// <summary>
/// <c>transcript.jsonl</c> — the recognised speech, appended one segment per
/// line as it arrives.
///
/// It follows <see cref="MarkJournal"/>'s durability contract rather than
/// session.json's: an fsync per write, so a meeting that ends in a crash
/// keeps every word it had already recognised. That matters more here than
/// for marks, because the audio can be re-transcribed but only at the cost of
/// running the whole meeting through whisper again.
///
/// Compiled into both editions. Lite never writes one, but it must still be
/// able to read a file a Full machine wrote and export it.
/// </summary>
public sealed class TranscriptStore : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _lock = new();

    public TranscriptStore(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public void Append(TranscriptSegment segment)
    {
        var line = "{\"start\":" + Number(segment.StartSeconds) +
                   ",\"end\":" + Number(segment.EndSeconds) +
                   ",\"p\":" + Number(segment.Probability) +
                   ",\"text\":" + JsonSerializer.Serialize(segment.Text) + "}";

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_lock)
        {
            _stream.Write(bytes, 0, bytes.Length);
            // Not Flush(): the point is to survive a power cut, so the OS
            // write-back cache has to reach the device too.
            _stream.Flush(true);
        }
    }

    /// <summary>Round-trippable, culture-invariant, never scientific notation for these ranges.</summary>
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        lock (_lock) _stream.Dispose();
    }

    /// <summary>Read a transcript back. A half-written last line is what a crash leaves; it is skipped.</summary>
    public static List<TranscriptSegment> Replay(string path)
    {
        var segments = new List<TranscriptSegment>();
        if (!File.Exists(path)) return segments;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    segments.Add(new TranscriptSegment
                    {
                        StartSeconds = root.GetProperty("start").GetDouble(),
                        EndSeconds = root.GetProperty("end").GetDouble(),
                        Probability = root.TryGetProperty("p", out var p) ? p.GetDouble() : 0,
                        Text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    });
                }
                catch (Exception)
                {
                    // Everything before the torn line is still good.
                }
            }
        }
        catch (Exception)
        {
            // An unreadable transcript costs the words, never the session.
        }

        return segments.OrderBy(s => s.StartSeconds).ToList();
    }
}
