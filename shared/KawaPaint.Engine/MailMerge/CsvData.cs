using System.Text;

namespace KawaPaint.Engine.MailMerge;

public sealed class CsvData
{
    public IReadOnlyList<string> Headers { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; private init; } =
        Array.Empty<IReadOnlyDictionary<string, string>>();

    public static CsvData Load(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public static CsvData Parse(string csv)
    {
        var records = ParseRecords(csv);
        if (records.Count == 0) throw new InvalidDataException("CSV has no header row.");
        string[] headers = records[0].Select((h, i) => i == 0 ? h.Trim().TrimStart('\uFEFF') : h.Trim()).ToArray();
        if (headers.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("CSV contains an empty header.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new InvalidDataException("CSV headers must be unique.");

        var rows = new List<IReadOnlyDictionary<string, string>>();
        for (int r = 1; r < records.Count; r++)
        {
            if (records[r].Count == 1 && records[r][0].Length == 0) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headers.Length; c++) row[headers[c]] = c < records[r].Count ? records[r][c] : "";
            rows.Add(row);
        }
        return new CsvData { Headers = headers, Rows = rows };
    }

    private static List<List<string>> ParseRecords(string csv)
    {
        char delimiter = DetectDelimiter(csv);
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char ch = csv[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) quoted = true;
            else if (ch == delimiter) { record.Add(field.ToString()); field.Clear(); }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear(); records.Add(record); record = new();
            }
            else field.Append(ch);
        }
        if (quoted) throw new InvalidDataException("CSV ends inside a quoted field.");
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
        return records;
    }

    private static char DetectDelimiter(string csv)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        bool quoted = false;
        foreach (char ch in csv)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && ch is '\r' or '\n') break;
            else if (!quoted && ch == ',') commas++;
            else if (!quoted && ch == ';') semicolons++;
            else if (!quoted && ch == '\t') tabs++;
        }
        if (tabs > commas && tabs > semicolons) return '\t';
        return semicolons > commas ? ';' : ',';
    }
}
