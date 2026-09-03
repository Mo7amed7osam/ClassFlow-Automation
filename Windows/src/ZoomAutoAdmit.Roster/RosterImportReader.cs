using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;

namespace ZoomAutoAdmit.Roster;

public static class RosterImportReader
{
    public static IReadOnlyList<Student> Read(string path, CancellationToken token = default)
        => ParseRows(ReadRaw(path, token), token);

    private static IEnumerable<string[]> ReadRaw(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > 20 * 1024 * 1024)
            throw new InvalidDataException("Import exceeds the 20 MB file limit.");
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv" => ReadCsv(path, token),
            ".xlsx" => ReadXlsx(path, token),
            _ => throw new InvalidDataException("Use UTF-8 CSV or Excel .xlsx. Legacy .xls is not supported.")
        };
    }

    public static IReadOnlyList<GroupStudent> ReadGroup(string path, string groupId, CancellationToken token = default)
    {
        Dictionary<string, int>? columns = null;
        var students = new List<GroupStudent>();
        var orders = new HashSet<int>();
        int rowNumber = 0;
        foreach (var row in ReadRaw(path, token))
        {
            token.ThrowIfCancellationRequested();
            rowNumber++;
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (columns == null)
            {
                columns = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < row.Length; i++)
                {
                    var header = row[i].Trim().Replace(" ", "").Replace("_", "").Replace("-", "");
                    if (header.Equals("name", StringComparison.OrdinalIgnoreCase)) header = "fullName";
                    if (header.Length > 0 && !columns.TryAdd(header, i)) throw new InvalidDataException("Duplicate import header.");
                }
                if (!columns.ContainsKey("order") || !columns.ContainsKey("fullName"))
                    throw new InvalidDataException("Group import requires Order and Name (or fullName) headers.");
                continue;
            }
            string Field(string key) => columns.TryGetValue(key, out int index) && index < row.Length ? row[index] : "";
            if (!int.TryParse(Field("order"), out int order) || order <= 0 || !orders.Add(order))
                throw new InvalidDataException($"Import row {rowNumber}: Order must be a unique positive integer.");
            var rowGroup = Field("groupId").Trim();
            if (rowGroup.Length > 0 && rowGroup != groupId) throw new InvalidDataException($"Import row {rowNumber}: different groupId.");
            try
            {
                var student = StudentValidation.Normalize(new(
                    string.IsNullOrWhiteSpace(Field("studentId")) ? Guid.NewGuid().ToString("D") : Field("studentId"),
                    Field("fullName"), Field("aliases").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Field("email")));
                students.Add(new(student.StudentId, groupId, order, student.FullName, student.Aliases, student.Email));
            }
            catch (ArgumentException ex) { throw new InvalidDataException($"Import row {rowNumber}: {ex.Message}", ex); }
            if (students.Count > 100000) throw new InvalidDataException("Import is limited to 100,000 students per file.");
        }
        if (students.Count == 0) throw new InvalidDataException("Import contains no student rows.");
        return students.OrderBy(s => s.Order).ToArray();
    }

    private static IEnumerable<string[]> ReadCsv(string path, CancellationToken token)
    {
        using var parser = new TextFieldParser(path, Encoding.UTF8, detectEncoding: true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        while (!parser.EndOfData)
        {
            token.ThrowIfCancellationRequested();
            yield return parser.ReadFields() ?? [];
        }
    }

    private static IReadOnlyList<Student> ParseRows(IEnumerable<string[]> rows, CancellationToken token)
    {
        Dictionary<string, int>? columns = null;
        var students = new List<Student>();
        int rowNumber = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            rowNumber++;
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (columns == null)
            {
                columns = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < row.Length; i++)
                {
                    string header = row[i].Trim().Replace(" ", "").Replace("_", "").Replace("-", "");
                    if (header.Length > 0 && !columns.TryAdd(header, i))
                        throw new InvalidDataException($"Duplicate import header: {row[i]}.");
                }
                if (!columns.ContainsKey("studentId") || !columns.ContainsKey("fullName"))
                    throw new InvalidDataException("Import must have studentId and fullName headers; aliases and email are optional.");
                continue;
            }
            string Field(string name) => columns.TryGetValue(name, out var index) && index < row.Length ? row[index] : "";
            try
            {
                students.Add(StudentValidation.Normalize(new(Field("studentId"), Field("fullName"),
                    Field("aliases").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Field("email"))));
            }
            catch (ArgumentException ex) { throw new InvalidDataException($"Import row {rowNumber}: {ex.Message}", ex); }
            if (students.Count > 100000) throw new InvalidDataException("Import is limited to 100,000 students.");
        }
        if (students.Count == 0) throw new InvalidDataException("Import contains no student rows.");
        return students;
    }

    private static IEnumerable<string[]> ReadXlsx(string path, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 2000 || archive.Entries.Sum(e => e.Length) > 64L * 1024 * 1024)
            throw new InvalidDataException("Expanded Excel workbook exceeds safety limits.");
        XDocument ReadXml(string name)
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Missing workbook part: {name}.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024
            });
            return XDocument.Load(reader);
        }
        XNamespace sheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace officeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var workbook = ReadXml("xl/workbook.xml");
        var first = workbook.Descendants(sheetNs + "sheet").FirstOrDefault()
            ?? throw new InvalidDataException("Workbook has no worksheets.");
        var id = (string?)first.Attribute(officeRel + "id");
        var relation = ReadXml("xl/_rels/workbook.xml.rels").Descendants(packageRel + "Relationship")
            .SingleOrDefault(r => (string?)r.Attribute("Id") == id)
            ?? throw new InvalidDataException("Worksheet relationship is missing.");
        if ((string?)relation.Attribute("TargetMode") == "External" ||
            !((string?)relation.Attribute("Type") ?? "").EndsWith("/worksheet", StringComparison.Ordinal))
            throw new InvalidDataException("Only local worksheet data is supported.");
        var target = (string?)relation.Attribute("Target") ?? throw new InvalidDataException("Worksheet target is missing.");
        var segments = new List<string>();
        foreach (var part in (target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target).Split('/'))
        {
            if (part == "." || part.Length == 0) continue;
            if (part == "..")
            {
                if (segments.Count == 0) throw new InvalidDataException("Invalid worksheet path.");
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(part);
        }
        var shared = archive.GetEntry("xl/sharedStrings.xml") == null ? [] : ReadXml("xl/sharedStrings.xml")
            .Descendants(sheetNs + "si").Select(s => string.Concat(s.Descendants(sheetNs + "t").Select(t => t.Value))).ToArray();
        var sheet = ReadXml(string.Join('/', segments));
        foreach (var row in sheet.Descendants(sheetNs + "sheetData").Elements(sheetNs + "row"))
        {
            token.ThrowIfCancellationRequested();
            var cells = new Dictionary<int, string>();
            foreach (var cell in row.Elements(sheetNs + "c"))
            {
                if (cell.Element(sheetNs + "f") != null)
                    throw new InvalidDataException("Excel formulas are not imported. Paste values into the first worksheet first.");
                string reference = (string?)cell.Attribute("r") ?? throw new InvalidDataException("Excel cell reference is missing.");
                int column = 0;
                foreach (char c in reference.TakeWhile(char.IsLetter))
                {
                    if (c is < 'A' or > 'Z') throw new InvalidDataException("Invalid Excel column.");
                    column = checked(column * 26 + c - 'A' + 1);
                    if (column > 16384) throw new InvalidDataException("Excel column exceeds safety limit.");
                }
                if (column == 0) throw new InvalidDataException("Invalid Excel cell reference.");
                string value = cell.Element(sheetNs + "v")?.Value ?? "";
                switch ((string?)cell.Attribute("t"))
                {
                    case "s":
                        if (!int.TryParse(value, out var index) || index < 0 || index >= shared.Length)
                            throw new InvalidDataException("Invalid Excel shared string reference.");
                        value = shared[index];
                        break;
                    case "inlineStr": value = string.Concat(cell.Descendants(sheetNs + "t").Select(t => t.Value)); break;
                    case "e": throw new InvalidDataException("Excel cell contains an error.");
                }
                if (!cells.TryAdd(column - 1, value)) throw new InvalidDataException("Duplicate Excel cell reference.");
            }
            var fields = new string[cells.Count == 0 ? 0 : cells.Keys.Max() + 1];
            Array.Fill(fields, "");
            foreach (var cell in cells) fields[cell.Key] = cell.Value;
            yield return fields;
        }
    }
}
