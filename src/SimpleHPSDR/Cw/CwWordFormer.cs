using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace SimpleHPSDR.Cw;

/// <summary>
/// Puts back word spaces a CW decoder missed: "CQSSK1XMK1XM" becomes "CQ SS K1XM K1XM",
/// "73AE0QDE" becomes "73 AE0Q DE". A run of letters is split only when every piece is
/// something known: a word from cw-words.txt, a callsign, a report, a number or a grid
/// square. Anything else is left as the decoder printed it, so garbled copy isn't
/// chopped into nonsense. Callsigns already seen standing alone in the text are
/// preferred, which settles most ambiguous runs.
///
/// <see cref="Form"/> works on the whole text each time, so earlier lines can tidy up
/// once a callsign has been seen. The last word is left alone until a space follows it.
/// </summary>
public static partial class CwWordFormer
{
    private static readonly HashSet<string> Words = LoadWords();

    // Sent so often that a split producing them is very likely right.
    private static readonly HashSet<string> Frequent =
        ["CQ", "DE", "K", "KN", "BK", "TU", "TNX", "UR", "RST", "ES", "R", "73", "5NN", "599", "NR", "QTH", "NAME", "OP", "HR", "FB", "GM", "GA", "GE", "SS", "TEST", "PSE", "AGN", "EE"];

    private const int MinLength = 4;     // shorter runs are left as they are
    private const int MaxPieceLength = 12;

    [GeneratedRegex(@"^(?:[A-Z0-9]{1,4}/)?(?:[A-Z]{1,2}|[A-Z][0-9]|[0-9][A-Z])[0-9][A-Z]{1,4}(?:/[A-Z0-9]{1,4})?$")]
    private static partial Regex Callsign();
    [GeneratedRegex(@"^(?:[0-9]{1,5}|[0-9]+\.[0-9]+|[0-9]{4}Z|[1-5][1-9][1-9N]|[1-5]NN|[0-9]{1,4}[ABQMSU])$")]
    private static partial Regex Number();
    [GeneratedRegex(@"^[A-R]{2}[0-9]{2}(?:[A-X]{2})?$")]
    private static partial Regex Grid();

    /// <summary>The decoded text with missing word spaces put back.</summary>
    public static string Form(string text)
    {
        if (text.Length == 0) return text;
        var tokens = text.Split(' ');
        bool lastComplete = text.EndsWith(' ');
        // Callsigns the decoder already printed on their own, last word aside. A run
        // doesn't count as seen just because of itself ("TU5NNNY" fits a callsign).
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < tokens.Length - (lastComplete ? 0 : 1); i++)
        {
            var t = Trim(tokens[i]);
            if (t.Length >= 3 && Callsign().IsMatch(t)) counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        var all = counts.Keys.ToHashSet();
        var twice = counts.Where(kv => kv.Value >= 2).Select(kv => kv.Key).ToHashSet();
        var sb = new StringBuilder(text.Length + 32);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            bool open = i == tokens.Length - 1 && !lastComplete;
            var self = Trim(tokens[i]);
            sb.Append(open ? tokens[i] : Split(tokens[i], counts.ContainsKey(self) && !twice.Contains(self)
                ? all.Where(c => c != self).ToHashSet() : all));
        }
        return sb.ToString();
    }

    /// <summary>One run of characters, split if every piece is known.</summary>
    private static string Split(string token, HashSet<string> seen)
    {
        if (token.Length < MinLength || Certain(Trim(token), seen)) return token;
        int n = token.Length;
        var best = new double[n + 1];
        var from = new int[n + 1];
        Array.Fill(best, double.PositiveInfinity);
        best[0] = 0;
        for (int end = 1; end <= n; end++)
            for (int start = Math.Max(0, end - MaxPieceLength); start < end; start++)
            {
                if (double.IsPositiveInfinity(best[start])) continue;
                var piece = token[start..end];
                // Punctuation may only close a piece ("QRZ?" or "FB.").
                if (piece.Length > 1 && piece[..^1].Any(c => c is '?' or '.' or ',')) continue;
                if (Cost(Trim(piece), seen) is not { } cost) continue;
                if (best[start] + cost < best[end])
                {
                    best[end] = best[start] + cost;
                    from[end] = start;
                }
            }
        if (double.IsPositiveInfinity(best[n])) return token;
        var pieces = new List<string>();
        for (int end = n; end > 0; end = from[end]) pieces.Add(token[from[end]..end]);
        pieces.Reverse();
        // Mostly little two-letter scraps ("LONG IS LA ND") means the run wasn't
        // really made of words: leave it.
        int scraps = pieces.Count(p => Trim(p) is var t && t.Length <= 2 && !Frequent.Contains(t) && !t.All(char.IsAsciiDigit));
        if (pieces.Count > 1 && scraps * 2 > pieces.Count) return token;
        return string.Join(' ', pieces);
    }

    /// <summary>A whole run that's surely right as it is: a seen callsign, a word or a number.
    /// A callsign or grid shape alone isn't enough ("TU5NNNY" fits a callsign).</summary>
    private static bool Certain(string piece, HashSet<string> seen) =>
        seen.Contains(piece) || Frequent.Contains(piece) || Words.Contains(piece) || Number().IsMatch(piece);

    /// <summary>How unlikely a piece is (lower is likelier), or null if it isn't known.</summary>
    private static double? Cost(string piece, HashSet<string> seen)
    {
        if (piece.Length == 0) return null;
        if (seen.Contains(piece)) return 0.3;
        if (Frequent.Contains(piece)) return piece is "K" ? 0.8 : piece is "R" ? 1.0 : 0.6;
        if (Words.Contains(piece)) return piece.Length > 1 ? 1.0 : piece is "E" or "T" ? 2.0 : 1.4;   // a lone E or T is often noise
        if (Number().IsMatch(piece)) return 1.2;
        if (Grid().IsMatch(piece)) return 2.6;   // grid shapes turn up by chance ("NR17", "EE44EE")
        if (piece.Length >= 3 && Callsign().IsMatch(piece)) return CallsignCost(piece, seen);
        return null;
    }

    private static double CallsignCost(string piece, HashSet<string> seen)
    {
        // A known call with a word stuck on ("AE0QDE", "KC2RGWK") is the call and the word.
        for (int cut = piece.Length - 1; cut >= 3; cut--)
        {
            string head = piece[..cut], tail = piece[cut..];
            if (Frequent.Contains(tail) && (seen.Contains(head) || Callsign().IsMatch(head))) return 2.6;
        }
        int suffix = piece.Length - 1 - piece.LastIndexOfAny("0123456789".ToCharArray());
        return suffix >= 4 ? 2.6 : 1.6;   // four-letter suffixes are rare
    }

    private static string Trim(string piece) => piece.TrimEnd('?', '.', ',');

    private static HashSet<string> LoadWords()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("cw-words.txt");
        if (s is null) return set;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) continue;
            foreach (var w in line.Split(' ', StringSplitOptions.RemoveEmptyEntries)) set.Add(w.ToUpperInvariant());
        }
        return set;
    }
}
