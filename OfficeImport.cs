using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

sealed record Imported(string GroupNumber, string Level, int Day, int Slot, int Subgroup, int Week,
    string Subject, string Teacher, string Room, string Status, string Source, string Issue);

static class OfficeImport
{
    static readonly string[] Days = ["ПОНЕДЕЛЬНИК", "ВТОРНИК", "СРЕДА", "ЧЕТВЕРГ", "ПЯТНИЦА", "СУББОТА", "ВОСКРЕСЕНЬЕ"];
    static readonly Regex Teacher = new(@"[\p{L}Ёё-]+\s+[\p{L}]\.\s*[\p{L}]\.?$", RegexOptions.IgnoreCase);
    static readonly Regex TeacherContinuation = new(@"^[\p{L}-]+\s+(?:\p{L}\.\s*\.|\p{L}{2}\.)$", RegexOptions.IgnoreCase);
    static readonly Regex Subgroup = new(@"([12])\s*[пр]\s*г\.?", RegexOptions.IgnoreCase);
    static readonly Regex Group = new(@"^[\p{L}\d]+\s*[-–]\s*\d+[\p{L}]?$");

    static XDocument Xml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException("Отсутствует " + path);
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Слишком большой XML.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
    static IEnumerable<XElement> Items(XContainer element, string name) => element.Descendants().Where(e => e.Name.LocalName == name);
    static string Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";
    static string Text(XContainer e) => string.Concat(Items(e, "t").Select(t => t.Value));
    static string CleanGroup(string value) => Regex.Replace(value.Trim().ToUpperInvariant().Replace('–', '-'), @"\s+", "");
    static string Join(string first, string second) => string.Join(' ', new[] { first.Trim(), second.Trim() }.Where(x => x.Length > 0));
    static string Issue(params string[] items) => string.Join(" · ", items.Where(x => x.Length > 0).Distinct());
    static int Day(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    public static int[] Slots(string raw)
    {
        var value = raw.Trim().Replace('–', '-').Replace('—', '-');
        if (Regex.IsMatch(value, @"^\d+\.\d{9,}$") && double.TryParse(value, CultureInfo.InvariantCulture, out var number))
            value = number.ToString("0.0", CultureInfo.InvariantCulture);
        if (!Regex.IsMatch(value, @"^\d{1,2}(\s*[.,;/-]\s*\d{1,2})*$")) return [];
        var results = new List<int>();
        foreach (var piece in Regex.Split(value, @"[.,;/]"))
        {
            var parts = piece.Split('-').Select(int.Parse).ToArray();
            if (parts.Any(n => n is < 1 or > 24) || parts.Length > 2 || parts[^1] < parts[0]) return [];
            results.AddRange(Enumerable.Range(parts[0], parts[^1] - parts[0] + 1));
        }
        return results.Distinct().ToArray();
    }

    static (int Row, int Col) Cell(string address)
    {
        var col = 0;
        var length = 0;
        foreach (var ch in address.TakeWhile(char.IsLetter)) { col = col * 26 + ch - 'A' + 1; length++; }
        return (int.Parse(address[length..], CultureInfo.InvariantCulture), col);
    }
    static string Address(int row, int col)
    {
        var letters = "";
        while (col > 0) { col--; letters = (char)('A' + col % 26) + letters; col /= 26; }
        return letters + row;
    }

    public static (List<Imported> Rows, HashSet<(string Number, string Level)> Groups, List<string> Warnings) Xlsx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var workbook = Xml(zip, "xl/workbook.xml");
        var relationships = Items(Xml(zip, "xl/_rels/workbook.xml.rels"), "Relationship").ToDictionary(e => Attr(e, "Id"), e => Attr(e, "Target"));
        var styles = zip.GetEntry("xl/styles.xml") is null ? new XDocument() : Xml(zip, "xl/styles.xml");
        var formats = Items(styles, "cellXfs").FirstOrDefault()?.Elements().ToArray() ?? [];

        var numberFormats = Items(styles, "numFmt").ToDictionary(e => Attr(e, "numFmtId"), e => Attr(e, "formatCode"));
        var strings = zip.GetEntry("xl/sharedStrings.xml") is null ? [] :
            Items(Xml(zip, "xl/sharedStrings.xml"), "si").Select(Text).ToArray();
        var result = new List<Imported>();
        var warnings = new List<string>();
        var groups = new HashSet<(string Number, string Level)>();
        var sheets = Items(workbook, "sheet").Where(s => Attr(s, "name") is "ПТО" or "ССО").ToList();
        if (sheets.Select(s => Attr(s, "name")).Distinct().Count() != 2) throw new InputError("В XLSX нужны листы «ПТО» и «ССО».");
        foreach (var sheet in sheets)
        {
            var level = Attr(sheet, "name");
            var target = relationships[Attr(sheet, "id")];
            var doc = Xml(zip, target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target);
            var sheetCells = Items(doc, "c").ToDictionary(c => Cell(Attr(c, "r")));
            XElement? Format(XElement cell) => int.TryParse(Attr(cell, "s"), out var i) ? formats.ElementAtOrDefault(i) : null;

            var cells = sheetCells.ToDictionary(pair => pair.Key, pair =>
            {
                var c = pair.Value;
                var value = Items(c, "v").FirstOrDefault()?.Value ?? "";
                if (Attr(c, "t") is "" or "n" && Format(c) is { } format &&
                    numberFormats.TryGetValue(Attr(format, "numFmtId"), out var code) && Regex.IsMatch(code, @"^0{2,8}$") &&
                    int.TryParse(value, CultureInfo.InvariantCulture, out var integer))
                    value = integer.ToString(code, CultureInfo.InvariantCulture);
                return (Attr(c, "t") switch { "s" => strings[int.Parse(value)], "inlineStr" => Text(c), _ => value }).Trim();
            });
            string Read(int r, int c) => cells.GetValueOrDefault((r, c), "");
            var ranges = Items(doc, "mergeCell").Select(e => Attr(e, "ref").Split(':'))
                .Where(a => a.Length == 2).Select(a => (First: Cell(a[0]), Last: Cell(a[1]))).ToList();
            var headers = ranges.Where(m => m.First.Row == m.Last.Row && m.Last.Col - m.First.Col == 2
                && Group.IsMatch(Read(m.First.Row, m.First.Col))).OrderBy(m => m.First.Col).ToList();
            var days = ranges.Where(m => m.First.Col == 1 && m.Last.Col == 1 &&
                Days.Contains(Read(m.First.Row, 1).ToUpperInvariant())).OrderBy(m => m.First.Row).ToList();
            if (headers.Count == 0 || days.Count == 0) throw new InputError("На листе " + level + " не найдены блоки групп и дней.");
            foreach (var header in headers)
            foreach (var day in days)
            {
                var col = header.First.Col;
                var group = CleanGroup(Read(header.First.Row, col));
                groups.Add((group, level));
                var weekday = Array.IndexOf(Days, Read(day.First.Row, 1).ToUpperInvariant()) + 1;
                var boundaries = Enumerable.Range(day.First.Row, day.Last.Row - day.First.Row + 1)
                    .Where(r => r == day.First.Row || Read(r, col).Length > 0)
                    .Append(day.Last.Row + 1).ToArray();
                var blocks = boundaries.Zip(boundaries.Skip(1), (first, end) =>
                {
                    var number = Read(first, col);
                    var slots = Slots(number);
                    if (number.Contains('.') && slots.Zip(slots.Skip(1)).Any(pair => pair.Second < pair.First))
                        slots = [];
                    return (First: first, End: end, Number: number, Slots: slots);
                }).ToArray();
                var repeated = blocks.SelectMany(block => block.Slots).GroupBy(slot => slot)
                    .Where(slots => slots.Count() > 1).Select(slots => slots.Key).ToHashSet();
                var seen = new HashSet<int>();
                foreach (var (first, row, number, slots) in blocks)
                {
                    if (slots.Length == 0)
                    {
                        if (number.Length > 0 || Enumerable.Range(first, row - first).Any(r => Read(r, col + 1).Length > 0))
                            warnings.Add($"{level}!{Address(first, col)} · {group} · Пропущена запись. Номер урока: «{number}».");
                        continue;
                    }
                    var weeks = slots.ToDictionary(slot => slot,
                        slot => repeated.Contains(slot) ? (seen.Add(slot) ? 1 : 2) : 0);
                    var entries = new List<(string Subject, string Teacher, string Room, int Sub, int Row)>();
                    var common = "";
                    for (var i = first; i < row; i++)
                    {
                        var line = Read(i, col + 1);
                        var room = Read(i, col + 2);
                        if (line.Length == 0)
                        {
                            if (room.Length > 0 && entries.Count > 0)
                            {
                                var prev = entries[^1];
                                entries[^1] = (prev.Subject, prev.Teacher, Join(prev.Room, room), prev.Sub, prev.Row);
                            }
                            continue;
                        }
                        var parts = Regex.Split(line, @"\r?\n|\s*/\s*(?=[12]\s*[пр]г)", RegexOptions.IgnoreCase);
                        foreach (var part in parts.Where(s => !string.IsNullOrWhiteSpace(s)))
                        {
                            var sub = Subgroup.Match(part);
                            var subgroup = sub.Success ? int.Parse(sub.Groups[1].Value) : 0;
                            var value = Subgroup.Replace(part, "").Trim();
                            var teacher = Teacher.Match(value);
                            if (!teacher.Success && entries.Count > 0)
                                teacher = TeacherContinuation.Match(value);
                            var subject = teacher.Success ? value[..teacher.Index].Trim(' ', ',', ';') : value;
                            if (subject.Length == 0 && subgroup == 0 && entries.Count > 0)
                            {
                                var prev = entries[^1];
                                entries[^1] = (prev.Subject, Join(prev.Teacher, teacher.Value), Join(prev.Room, room), prev.Sub, prev.Row);
                            }
                            else if (subject.Length == 0 && subgroup > 0 && entries.Count > 0 && entries[0].Sub == 0)
                            {
                                common = entries[0].Subject;
                                entries.RemoveAt(0);
                                entries.Add((common, teacher.Value, room, subgroup, i));
                            }
                            else entries.Add((subject.Length == 0 ? common : subject, teacher.Value, room, subgroup, i));
                        }
                    }
                    if (entries.Count == 0) continue;
                    foreach (var entry in entries)
                    {
                        if (entry.Subject.Equals("ОБЕД", StringComparison.OrdinalIgnoreCase)) continue;
                        var problem = Issue(
                            entries.Count(x => x.Sub == entry.Sub) > 1 ? "Проверьте чередование недель и несколько записей в блоке." : "",
                            entry.Subject.Length == 0 ? "Укажите предмет." : "");
                        foreach (var slot in slots)
                            result.Add(new(group, level, weekday, slot, entry.Sub, weeks[slot], entry.Subject, entry.Teacher,
                                entry.Room, Status(entry.Subject), level + "!" + Address(entry.Row, col + 1), problem));
                    }
                }
            }
        }
        return (result, groups, warnings);
    }

    static string Status(string title) => title.Trim().Equals("УРОКА НЕТ", StringComparison.OrdinalIgnoreCase) ? "cancel" : "lesson";
    public static (DateOnly Date, int Week, List<Imported> Rows, List<string> Warnings) Docx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var doc = Xml(zip, "word/document.xml");
        var body = Items(doc, "body").First();
        var header = string.Join(" ", body.Elements().TakeWhile(e => e.Name.LocalName != "tbl").Select(Text));
        var match = Regex.Match(header, @"\b\d{1,2}\.\d{1,2}\.\d{4}");
        if (!DateOnly.TryParseExact(match.Value, ["d.M.yyyy", "dd.MM.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InputError("В заголовке DOCX не найдена дата замены.");
        var week = Regex.IsMatch(header, @"\bНАД\s+ЧЕРТОЙ", RegexOptions.IgnoreCase) ? 1 :
            Regex.IsMatch(header, @"\bПОД\s+ЧЕРТОЙ", RegexOptions.IgnoreCase) ? 2 : 0;
        var result = new List<Imported>();
        var warnings = new List<string>();
        var lineNo = 0;
        foreach (var row in Items(body, "tr"))
        {
            lineNo++;
            var columns = row.Elements().Where(e => e.Name.LocalName == "tc").Select(cell =>
                cell.Elements().Where(e => e.Name.LocalName == "p").SelectMany(p =>
                    Regex.Split(string.Concat(p.Descendants().Where(e => e.Name.LocalName is "t" or "br")
                        .Select(e => e.Name.LocalName == "br" ? "\n" : e.Value)), "\n")).Select(x => x.Trim()).ToArray()).ToArray();
            if (columns.Length == 0) continue;
            string At(int col, int line) => columns.ElementAtOrDefault(col)?.ElementAtOrDefault(line) ?? "";
            var group = CleanGroup(Regex.Replace(At(0, 0), @"^гр\.?\s*", "", RegexOptions.IgnoreCase));
            if (!Group.IsMatch(group))
            {
                if (columns.Length >= 6 && columns.SelectMany(a => a).Any(x => x.Length > 0) &&
                    !At(0, 0).Contains("группа", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"DOCX, строка {lineNo} · Пропущена запись с нераспознанной группой «{At(0, 0)}».");
                continue;
            }
            var length = columns.Skip(1).Select(a => a.Length).DefaultIfEmpty().Max();
            for (var index = 0; index < length; index++)
            {
                var number = At(1, index);
                var rawSub = At(2, index);
                var title = At(3, index);
                var teacher = At(4, index);
                var room = At(5, index);
                if (new[] { number, rawSub, title, teacher, room }.All(string.IsNullOrEmpty)) continue;
                var practice = number.Length == 0 && Regex.IsMatch(title, @"^(учебная\s+)?практика\b", RegexOptions.IgnoreCase);
                var slots = Slots(number);
                if (slots.Length == 0 && !practice)
                {
                    warnings.Add($"DOCX, строка {lineNo}, позиция {index + 1} · {group} · Пропущена запись. Номер урока: «{number}».");
                    continue;
                }
                var subgroups = Subgroup.Matches(rawSub).Select(m => int.Parse(m.Groups[1].Value)).Distinct().DefaultIfEmpty(0).ToArray();
                var subjects = subgroups.Length > 1 ? title.Split('/') : [title];
                var teachers = subgroups.Length > 1 ? teacher.Split('/') : [teacher];
                var rooms = subgroups.Length > 1 && room.Contains('/') && !room.Equals("с/з", StringComparison.OrdinalIgnoreCase) ? room.Split('/') : [room];
                var problem = Issue(columns.Length != 6 ? "Проверьте структуру строки." : "",
                    practice ? "Подтвердите практику на весь день." : "",
                    rawSub.Length > 0 && subgroups[0] == 0 ? "Проверьте подгруппу: " + rawSub : "",
                    title.Length == 0 ? "Укажите предмет или примечание." : "");
                for (var i = 0; i < subgroups.Length; i++)
                foreach (var slot in slots.DefaultIfEmpty(0))
                {
                    string Value(string[] values) => values.Length == 1 ? values[0] : (values.ElementAtOrDefault(i) ?? "").Trim();
                    result.Add(new(group, "", Day(date), slot, subgroups[i], 0, Value(subjects), Value(teachers), Value(rooms),
                        practice ? "practice" : Status(Value(subjects)), "DOCX, строка " + lineNo + ", позиция " + (index + 1), problem));
                }
            }
        }
        return (date, week, result, warnings);
    }
}

