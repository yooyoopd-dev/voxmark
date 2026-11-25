using System.Text.RegularExpressions;

namespace MeetingRecorder.Services;

/// <summary>
/// The operator's dictionary: the vocabulary whisper is told to expect, and
/// the corrections applied to what it recognised anyway.
///
/// The two do different jobs. Vocabulary goes into whisper's prompt and
/// <em>biases</em> the decoder — it helps, and nothing proves it helped. A
/// correction is a rule, "heard => correct", applied to the recognised text
/// afterwards: it is deterministic, it is counted on the strip, and it is the
/// only way to fix a word the model keeps mishearing the same way however it
/// is prompted. A correction's right-hand side also joins the prompt, so one
/// line in Settings works on both ends.
///
/// Both are kept per language, English and Korean, because the lists do not
/// transfer: an English prompt full of Korean names pulls an English decode
/// off course, and the other way round. A meeting recognised as anything else
/// (auto-detect included) gets both lists, since nothing says which applies.
///
/// It also turns the operator's own edits into suggestions:
/// <see cref="Diff"/> compares a line as recognised with the line as
/// corrected and returns the words that were swapped, which is what the
/// export screen offers to add here.
///
/// Compiled into both editions and names no Whisper.net type.
/// </summary>
public static class TranscriptDictionary
{
    /// <summary>One rule: text recognised as <paramref name="Heard"/> is rewritten to <paramref name="Correct"/>.</summary>
    public sealed record Correction(string Heard, string Correct);

    /// <summary>A correction the operator made by hand, and how many lines they made it on.</summary>
    public sealed record Suggestion(string Heard, string Correct, int Count);

    /// <summary>Corrections compiled for the recognition thread. Immutable once built.</summary>
    public sealed class Rules
    {
        internal readonly List<(Regex Pattern, string Replacement)> Items = new();

        public int Count => Items.Count;
    }

    /// <summary>
    /// A hand edit that swapped more words than this is a rewrite of the
    /// sentence, not a misheard term, and is not offered as a rule.
    /// </summary>
    private const int MaxSuggestionWords = 4;

    private static readonly string[] Separators = { "=>", "->", "→", "⇒" };

    private static readonly char[] Punctuation =
    {
        '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '…', '“', '”', '‘', '’', '·', '~',
    };

    /// <summary>"en" or "ko" for a language that has its own list; empty for one that gets both.</summary>
    public static string ListFor(string? language) => (language ?? "").Trim().ToLowerInvariant() switch
    {
        "en" => "en",
        "ko" => "ko",
        _ => "",
    };

    public static string LanguageName(string list) => list == "ko" ? "Korean" : "English";

    /// <summary>The vocabulary a meeting in <paramref name="language"/> is prompted with.</summary>
    public static string VocabularyFor(TranscriptionSettingsStore.Settings settings, string? language) =>
        ListFor(language) switch
        {
            "en" => settings.Vocabulary ?? "",
            "ko" => settings.VocabularyKo ?? "",
            _ => (settings.Vocabulary ?? "") + "\n" + (settings.VocabularyKo ?? ""),
        };

    /// <summary>The corrections text a meeting in <paramref name="language"/> is rewritten with.</summary>
    public static string CorrectionsFor(TranscriptionSettingsStore.Settings settings, string? language) =>
        ListFor(language) switch
        {
            "en" => settings.Corrections ?? "",
            "ko" => settings.CorrectionsKo ?? "",
            _ => (settings.Corrections ?? "") + "\n" + (settings.CorrectionsKo ?? ""),
        };

    /// <summary>Store one language's lists. <paramref name="list"/> is "en" or "ko".</summary>
    public static void Set(TranscriptionSettingsStore.Settings settings, string list, string vocabulary, string corrections)
    {
        if (list == "ko")
        {
            settings.VocabularyKo = vocabulary;
            settings.CorrectionsKo = corrections;
        }
        else
        {
            settings.Vocabulary = vocabulary;
            settings.Corrections = corrections;
        }
    }

    /// <summary>
    /// Which list a hand-made correction belongs in: the meeting's own when it
    /// was recognised as English or Korean, otherwise wherever its script
    /// puts it — Hangul on either side makes it a Korean rule.
    /// </summary>
    public static string ListForSuggestion(string? sessionLanguage, string heard, string correct)
    {
        var list = ListFor(sessionLanguage);
        if (list.Length > 0) return list;
        return HasHangul(heard) || HasHangul(correct) ? "ko" : "en";
    }

    private static bool HasHangul(string text) =>
        text.Any(c => c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏');

    // ------------------------------------------------------------- the rules

    /// <summary>
    /// Read corrections, one per line, written <c>heard =&gt; correct</c>
    /// (<c>-&gt;</c> and <c>→</c> are accepted too). A line without a
    /// separator, or with either side empty, is ignored rather than guessed at.
    /// </summary>
    public static List<Correction> Parse(string? text)
    {
        var corrections = new List<Correction>();
        if (string.IsNullOrWhiteSpace(text)) return corrections;

        foreach (var line in text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var separator in Separators)
            {
                var at = line.IndexOf(separator, StringComparison.Ordinal);
                if (at <= 0) continue;

                var heard = line[..at].Trim();
                var correct = line[(at + separator.Length)..].Trim();
                if (heard.Length > 0 && correct.Length > 0 && !string.Equals(heard, correct, StringComparison.Ordinal))
                {
                    corrections.Add(new Correction(heard, correct));
                }
                break;
            }
        }

        return corrections;
    }

    /// <summary>The line a correction is stored as.</summary>
    public static string Format(Correction correction) => correction.Heard + " => " + correction.Correct;

    /// <summary>Is this rule (same heard text, same replacement) already in the list?</summary>
    public static bool Contains(string? correctionsText, Correction correction) =>
        Parse(correctionsText).Any(c =>
            string.Equals(c.Heard, correction.Heard, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Correct, correction.Correct, StringComparison.Ordinal));

    /// <summary>The corrections text with one more rule on the end.</summary>
    public static string Append(string? correctionsText, Correction correction)
    {
        var text = (correctionsText ?? "").TrimEnd();
        return text.Length == 0 ? Format(correction) : text + "\n" + Format(correction);
    }

    /// <summary>
    /// Compile rules for <see cref="Apply"/>. Longest first, so a rule for
    /// "in video card" is tried before one for "in video".
    ///
    /// Matching ignores case. A side of the heard text that ends in a Latin
    /// letter or digit must end on a word boundary — "art" does not fire
    /// inside "start" — but Hangul gets no such guard, because Korean attaches
    /// its particles: a rule for "엔비디아" has to fire inside "엔비디아가".
    /// </summary>
    public static Rules Compile(IEnumerable<Correction> corrections)
    {
        var rules = new Rules();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var correction in corrections.OrderByDescending(c => c.Heard.Length))
        {
            if (!seen.Add(correction.Heard)) continue;
            try
            {
                var words = correction.Heard
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Regex.Escape);
                var body = string.Join(@"\s+", words);
                if (body.Length == 0) continue;

                var pattern = (IsLatinWordChar(correction.Heard[0]) ? "(?<![A-Za-z0-9])" : "") +
                              body +
                              (IsLatinWordChar(correction.Heard[^1]) ? "(?![A-Za-z0-9])" : "");

                rules.Items.Add((
                    new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                              TimeSpan.FromMilliseconds(100)),
                    correction.Correct));
            }
            catch (Exception)
            {
                // A rule that cannot be compiled is skipped; the rest still apply.
            }
        }

        return rules;
    }

    private static bool IsLatinWordChar(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    /// <summary>
    /// Rewrite one recognised line. <paramref name="applied"/> counts the
    /// places text actually changed — a rule matching text that already reads
    /// correctly is not a fix. Never throws: it runs inside the recogniser's
    /// callback, and a bad rule must cost a correction, not the transcript.
    /// </summary>
    public static string Apply(string text, Rules? rules, out int applied)
    {
        applied = 0;
        if (rules is null || rules.Count == 0 || string.IsNullOrEmpty(text)) return text;

        var count = 0;
        foreach (var (pattern, replacement) in rules.Items)
        {
            try
            {
                text = pattern.Replace(text, match =>
                {
                    if (!string.Equals(match.Value, replacement, StringComparison.Ordinal)) count++;
                    return replacement;
                });
            }
            catch (Exception)
            {
                // Timed out or otherwise failed: leave the line as it was for this rule.
            }
        }

        applied = count;
        return text;
    }

    // ----------------------------------------------------- learning from edits

    /// <summary>
    /// The words that differ between a line as recognised and as the operator
    /// left it. Word-level, by longest common subsequence: each run that was
    /// replaced becomes one correction, and a run that was only inserted or
    /// only deleted becomes none — there is nothing "heard" to key a rule on,
    /// or nothing to rewrite it to. Punctuation-only changes are not
    /// corrections; a change of capitals is.
    /// </summary>
    public static List<Correction> Diff(string? original, string? edited)
    {
        var result = new List<Correction>();
        var a = Words(original);
        var b = Words(edited);
        if (a.Length == 0 || b.Length == 0 || a.Length > 400 || b.Length > 400) return result;

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var removed = new List<string>();
        var added = new List<string>();

        void Flush()
        {
            if (removed.Count is > 0 and <= MaxSuggestionWords && added.Count is > 0 and <= MaxSuggestionWords)
            {
                var heard = string.Join(" ", removed);
                var correct = string.Join(" ", added);
                if (heard.Length >= 2 && !string.Equals(heard, correct, StringComparison.Ordinal))
                {
                    result.Add(new Correction(heard, correct));
                }
            }
            removed.Clear();
            added.Clear();
        }

        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                Flush();
                x++;
                y++;
            }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                added.Add(b[y++]);
            }
            else
            {
                removed.Add(a[x++]);
            }
        }
        Flush();

        return result;
    }

    /// <summary>A line as words, each stripped of the punctuation around it.</summary>
    private static string[] Words(string? text) =>
        (text ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim(Punctuation))
            .Where(word => word.Length > 0)
            .ToArray();

    /// <summary>
    /// Every hand correction in a session, most-repeated first. The same swap
    /// made on five lines is one suggestion with a count of five — and the
    /// count is the argument for turning it into a rule.
    /// </summary>
    public static List<Suggestion> Suggest(IEnumerable<(string Original, string Final)> edits)
    {
        var counts = new Dictionary<(string Heard, string Correct), (Correction Rule, int Count)>();

        foreach (var (original, final) in edits)
        {
            foreach (var correction in Diff(original, final))
            {
                var key = (correction.Heard.ToLowerInvariant(), correction.Correct);
                counts[key] = counts.TryGetValue(key, out var existing)
                    ? (existing.Rule, existing.Count + 1)
                    : (correction, 1);
            }
        }

        return counts.Values
            .OrderByDescending(v => v.Count)
            .ThenBy(v => v.Rule.Heard, StringComparer.OrdinalIgnoreCase)
            .Select(v => new Suggestion(v.Rule.Heard, v.Rule.Correct, v.Count))
            .ToList();
    }

    /// <summary>How many terms and rules a language's lists hold, for the Settings read-back.</summary>
    public static (int Terms, int Rules) Measure(string? vocabulary, string? corrections)
    {
        var terms = (vocabulary ?? "")
            .Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return (terms, Parse(corrections).Count);
    }
}
