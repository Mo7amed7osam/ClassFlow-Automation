using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace ZoomAutoAdmit.WindowsUI.Services;

public sealed record ScheduleImportRow(int RowNumber, string SessionNumber, DateOnly? Date, string Type,
    string Topic, TimeOnly? StartTime, string TimeRange, string Issue)
{
    public bool CanImport => Issue.Length == 0 && Date.HasValue && StartTime.HasValue;
}
public sealed record ScheduleImportPreview(string GroupCode, IReadOnlyList<ScheduleImportRow> Rows);

/// <summary>Read-only DEPI timetable import; never evaluates formulas, macros or external links.</summary>
public static class ScheduleWorkbookReader
{
    public static ScheduleImportPreview Read(string path)
    {
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an Excel .xlsx timetable.");
        if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("Workbook exceeds 20 MB.");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 64L * 1024 * 1024)
            throw new InvalidDataException("Expanded workbook exceeds safety limits.");
        XDocument ReadXml(string name)
        {
            using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException("Missing workbook part.")).Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var book = ReadXml("xl/workbook.xml");
        var id = (string?)book.Descendants(ns + "sheet").FirstOrDefault()?.Attribute(rel + "id");
        var relationship = ReadXml("xl/_rels/workbook.xml.rels").Root!.Elements().SingleOrDefault(e => (string?)e.Attribute("Id") == id)
            ?? throw new InvalidDataException("Worksheet relationship is missing.");
        var target = (string?)relationship.Attribute("Target") ?? "";
        if ((string?)relationship.Attribute("TargetMode") == "External" || target.Contains("..") || target.Contains('\\') || target.Contains(':') ||
            !((string?)relationship.Attribute("Type") ?? "").EndsWith("/worksheet", StringComparison.Ordinal))
            throw new InvalidDataException("Only a local worksheet is supported.");
        var sheet = ReadXml(target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target);
        var shared = zip.GetEntry("xl/sharedStrings.xml") == null ? [] : ReadXml("xl/sharedStrings.xml").Descendants(ns + "si")
            .Select(s => string.Concat(s.Descendants(ns + "t").Select(t => t.Value))).ToArray();
        bool date1904 = (string?)book.Root?.Element(ns + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var data = new List<(int Row, Dictionary<string, string> Cells)>();
        foreach (var row in sheet.Descendants(ns + "sheetData").Elements(ns + "row"))
        {
            if (data.Count >= 10000) throw new InvalidDataException("Workbook exceeds 10,000 rows.");
            var cells = new Dictionary<string, string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                if (cell.Element(ns + "f") != null) throw new InvalidDataException("Paste formulas as values before importing.");
                string reference = (string?)cell.Attribute("r") ?? "";
                string column = string.Concat(reference.TakeWhile(c => c is >= 'A' and <= 'Z'));
                if (column.Length is 0 or > 3) throw new InvalidDataException("Invalid cell reference.");
                string value = cell.Element(ns + "v")?.Value ?? "";
                switch ((string?)cell.Attribute("t"))
                {
                    case "s":
                        if (!int.TryParse(value, out int index) || index < 0 || index >= shared.Length) throw new InvalidDataException("Invalid shared string.");
                        value = shared[index]; break;
                    case "inlineStr": value = string.Concat(cell.Descendants(ns + "t").Select(t => t.Value)); break;
                    case "e": throw new InvalidDataException("Workbook contains an Excel error.");
                }
                if (!cells.TryAdd(column, value.Trim())) throw new InvalidDataException("Duplicate cell.");
            }
            data.Add((int.Parse((string?)row.Attribute("r") ?? "0", CultureInfo.InvariantCulture), cells));
        }
        string group = "";
        for (int i = 0; i < data.Count - 1; i++)
        {
            var column = data[i].Cells.FirstOrDefault(c => c.Value.Equals("Round Code", StringComparison.OrdinalIgnoreCase)).Key;
            if (column != null) { group = data[i + 1].Cells.GetValueOrDefault(column, ""); break; }
        }
        int header = data.FindIndex(r => r.Cells.Values.Any(v => v.Equals("Session No.", StringComparison.OrdinalIgnoreCase)) &&
            r.Cells.Values.Any(v => v.Equals("Session Type", StringComparison.OrdinalIgnoreCase)));
        if (header < 0) throw new InvalidDataException("Expected the DEPI timetable headers: Session No., Date, Session Type, Content and Time.");
        string Column(params string[] names) => data[header].Cells.FirstOrDefault(c => names.Contains(c.Value, StringComparer.OrdinalIgnoreCase)).Key
            ?? throw new InvalidDataException("Missing timetable column: " + names[0]);
        var numberCol = Column("Session No."); var dateCol = Column("Date", "Session Date");
        var typeCol = Column("Session Type"); var topicCol = Column("Content", "Topic", "Session Content"); var timeCol = Column("Slot", "Time", "Session Time");
        var result = new List<ScheduleImportRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in data.Skip(header + 1))
        {
            string Field(string col) => row.Cells.GetValueOrDefault(col, "");
            if (row.Cells.Values.All(string.IsNullOrWhiteSpace)) continue;
            string issue = "", type = Field(typeCol), topic = Field(topicCol), number = Field(numberCol);
            DateOnly? date = ParseDate(Field(dateCol), date1904);
            string time = Field(timeCol);
            string start = time.Split(['-', '–', '—'])[0].Trim();
            TimeOnly? parsedTime = TimeOnly.TryParseExact(start, ["h:mm tt", "hh:mm tt", "H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : null;
            if (!type.Equals("Online", StringComparison.OrdinalIgnoreCase)) issue = "Excluded: " + (type.Length == 0 ? "unknown session type" : type);
            else if (!date.HasValue || !parsedTime.HasValue || number.Length == 0 || topic.Length == 0) issue = "Invalid/missing date, time, session number or topic";
            else if (!seen.Add($"{date}|{parsedTime}")) issue = "Duplicate date/time in workbook";
            result.Add(new(row.Row, number, date, type, topic, parsedTime, time, issue));
        }
        if (result.Count == 0) throw new InvalidDataException("No timetable rows found.");
        return new(group, result);
    }

    private static DateOnly? ParseDate(string value, bool date1904)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            if (serial < 1 || serial > 2950000 || serial != Math.Floor(serial)) return null;
            return DateOnly.FromDateTime(DateTime.FromOADate(serial + (date1904 ? 1462 : 0)));
        }
        return DateOnly.TryParseExact(value, ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
}
