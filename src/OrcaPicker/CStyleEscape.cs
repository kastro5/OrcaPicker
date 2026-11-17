using System.Text;

namespace OrcaPicker;

/// <summary>
/// Exact ports of OrcaSlicer's escape_strings_cstyle / unescape_strings_cstyle
/// (src/libslic3r/Config.cpp, see docs/orca-ipc.md §3).
/// Orca works on UTF-8 bytes. Every character it treats specially is ASCII, and UTF-8 multi-byte sequences
/// never contain ASCII bytes, so working on UTF-16 chars gives the same result.
/// </summary>
internal static class CStyleEscape
{
    public static string EscapeStrings(IReadOnlyList<string> strs)
    {
        var sb = new StringBuilder();
        for (int j = 0; j < strs.Count; j++)
        {
            if (j > 0)
                sb.Append(';');
            string str = strs[j];
            bool shouldQuote = strs.Count == 1 && str.Length == 0;
            foreach (char c in str)
            {
                if (c is ' ' or '\t' or ';' or '\\' or '"' or '\r' or '\n')
                {
                    shouldQuote = true;
                    break;
                }
            }
            if (!shouldQuote)
            {
                sb.Append(str);
                continue;
            }
            sb.Append('"');
            foreach (char c in str)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
        return sb.ToString();
    }

    /// <summary>Port of unescape_strings_cstyle. Returns false where Orca returns false.</summary>
    public static bool UnescapeStrings(string str, out List<string> output)
    {
        output = new List<string>();
        if (str.Length == 0)
            return true;

        int i = 0;
        while (true)
        {
            // Skip white spaces.
            char c = str[i];
            while (c is ' ' or '\t')
            {
                if (++i == str.Length)
                    return true;
                c = str[i];
            }
            // Start of a word.
            var buf = new StringBuilder();
            c = str[i];
            if (c == '"')
            {
                // Quoted: runs until the next unescaped quote.
                for (++i; i < str.Length; ++i)
                {
                    c = str[i];
                    if (c == '"')
                        break;
                    if (c == '\\')
                    {
                        if (++i == str.Length)
                            return false;
                        c = str[i];
                        if (c == 'r') c = '\r';
                        else if (c == 'n') c = '\n';
                    }
                    buf.Append(c);
                }
                if (i == str.Length)
                    return false;
                ++i;
            }
            else
            {
                for (; i < str.Length; ++i)
                {
                    c = str[i];
                    if (c == ';')
                        break;
                    buf.Append(c);
                }
            }
            output.Add(buf.ToString());
            if (i == str.Length)
                return true;
            // Skip white spaces.
            c = str[i];
            while (c is ' ' or '\t')
            {
                if (++i == str.Length)
                    return true;
                c = str[i];
            }
            if (c != ';')
                return false;
            if (++i == str.Length)
            {
                // A trailing ';' emits one additional empty string.
                output.Add(string.Empty);
                return true;
            }
        }
    }
}
