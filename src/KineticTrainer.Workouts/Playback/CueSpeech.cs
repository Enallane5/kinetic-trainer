using System.Text.RegularExpressions;

namespace KineticTrainer.Workouts.Playback;

/// <summary>
/// Turns on-screen cue text into words a speech synthesiser reads naturally. The screen keeps compact units
/// ("240 W", "125–175 W", "85% FTP"); the voice says "240 watts", "125 to 175 watts", "85 percent of F T P".
/// </summary>
public static partial class CueSpeech
{
    public static string ToSpoken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        var s = text;

        // Ranges first: "125–175 W", "50-70% FTP".
        s = WattRange().Replace(s, m => $"{m.Groups[1].Value} to {m.Groups[2].Value} {Watts(m.Groups[2].Value)}");
        s = PercentRange().Replace(s, "$1 to $2 percent");

        // Single values: "240 W", "1 W", "+25 W", "−15 W".
        s = WattValue().Replace(s, m => $"{m.Groups[1].Value} {Watts(m.Groups[1].Value)}");
        s = PercentValue().Replace(s, "$1 percent");

        s = FtpWord().Replace(s, "F T P");
        s = RpmWord().Replace(s, "R P M");
        s = KjWord().Replace(s, "kilojoules");
        s = s.Replace("% ", " percent ", StringComparison.Ordinal);
        s = s.Replace("percent F T P", "percent of F T P", StringComparison.Ordinal);
        s = s.Replace("×", " times ", StringComparison.Ordinal);
        s = s.Replace("−", "minus ", StringComparison.Ordinal);
        s = s.Replace("–", " to ", StringComparison.Ordinal);
        s = s.Replace("·", ",", StringComparison.Ordinal);
        s = MaxWord().Replace(s, "max");
        s = MultiSpace().Replace(s, " ");
        return s.Trim();
    }

    private static string Watts(string number) => number.TrimStart('+', '−', '-') == "1" ? "watt" : "watts";

    [GeneratedRegex(@"(\d+)\s*[–-]\s*(\d+)\s*W\b")]
    private static partial Regex WattRange();

    [GeneratedRegex(@"(\d+)\s*[–-]\s*(\d+)\s*%")]
    private static partial Regex PercentRange();

    [GeneratedRegex(@"([+−-]?\d+)\s*W\b")]
    private static partial Regex WattValue();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%(?=\s|$|[,.;:)])")]
    private static partial Regex PercentValue();

    [GeneratedRegex(@"\bFTP\b")]
    private static partial Regex FtpWord();

    [GeneratedRegex(@"\brpm\b", RegexOptions.IgnoreCase)]
    private static partial Regex RpmWord();

    [GeneratedRegex(@"\bkJ\b")]
    private static partial Regex KjWord();

    [GeneratedRegex(@"\bMAX\b")]
    private static partial Regex MaxWord();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
