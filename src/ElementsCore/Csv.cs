using System.Text;

namespace ElementsCore;

/// <summary>Minimal RFC 4180 CSV reader/writer (UTF-8 with BOM so Excel shows Unicode correctly).</summary>
public static class Csv
{
    public static string Escape(string? v)
    {
        v ??= "";
        bool quote = v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || v.Length != v.Trim().Length;
        return quote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static void WriteRow(TextWriter w, IEnumerable<string?> cells)
    {
        w.Write(string.Join(",", cells.Select(Escape)));
        w.Write("\r\n");
    }

    public static StreamWriter CreateWriter(string path) => new(path, false, new UTF8Encoding(true));

    public static IEnumerable<List<string>> Read(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQ = false;
                }
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQ = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    yield return row; row = new List<string>();
                    break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); yield return row; }
    }
}
