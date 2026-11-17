using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AlShifa.Api.Features.Reports;

/// <summary>
/// A report as a real Excel workbook: one right-to-left sheet with the report's name and days on top, its figures, then its
/// list under a coloured header with a row number, real numbers that add up in Excel, and a bold total. Written with the
/// zip support in .NET, so it needs no extra library.
/// </summary>
public static class ReportExcel
{
    private const string Font = "Tahoma";

    // Cell styles, by their index in styles.xml.
    private const int Hospital = 1, Title = 2, Subtitle = 3, Header = 4, TextCell = 5, IntCell = 6, DecimalCell = 7,
        TotalText = 8, TotalInt = 9, TotalDecimal = 10, TileLabel = 11, TileInt = 12, TileDecimal = 13, Note = 14, RowNumber = 15;

    public static byte[] Build(ReportDocument document, string? hospital)
    {
        var table = document.Tables.FirstOrDefault();
        var columns = table?.Columns ?? [];
        var rows = new List<string>();
        var merges = new List<string>();
        var widths = new double[columns.Count + 1];
        var lastColumn = Math.Max(columns.Count + 1, 2);
        var line = 0;

        void Width(int column, string text) => widths[Math.Min(column, widths.Length - 1)] = Math.Max(widths[Math.Min(column, widths.Length - 1)], text.Length);

        string Wide(int style, string text, double? height = null)
        {
            line++;
            merges.Add($"A{line}:{Letter(lastColumn - 1)}{line}");
            return RowXml(line, height, TextXml(0, line, text, style));
        }

        if (!string.IsNullOrWhiteSpace(hospital))
        {
            rows.Add(Wide(Hospital, hospital!, 22));
        }

        rows.Add(Wide(Title, document.Title, 30));
        rows.Add(Wide(Subtitle, $"{ReportText.Period(document.From, document.To)}   ·   گرفته‌شده در {ReportText.Time(DateTime.UtcNow)}", 20));
        line++;

        // The figures, one under another: the name beside its number, clear of the narrow row-number column.
        var at = columns.Count >= 2 ? 1 : 0;
        foreach (var tile in document.Tiles)
        {
            line++;
            var label = tile.Kind == ReportKind.Money ? $"{tile.Label} (افغانی)" : tile.Label;
            Width(at, label);
            rows.Add(RowXml(line, 22, TextXml(at, line, label, TileLabel) + NumberXml(at + 1, line, tile.Value, Whole(tile.Value) ? TileInt : TileDecimal)));
        }

        var headerLine = 0;
        var firstData = 0;
        var lastData = 0;
        if (table is not null)
        {
            line++;
            line++;
            headerLine = line;
            var header = new StringBuilder(TextXml(0, line, "ردیف", Header));
            for (var c = 0; c < columns.Count; c++)
            {
                var label = columns[c].Kind == ReportKind.Money ? $"{columns[c].Label} (افغانی)" : columns[c].Label;
                Width(c + 1, label);
                header.Append(TextXml(c + 1, line, label, Header));
            }

            rows.Add(RowXml(line, 28, header.ToString()));

            // A numeric column shows decimals only if one of its values has them.
            var whole = columns.Select((column, c) => table.Rows.All(r => ReportText.NumberOf(r[c]) is not { } n || Whole(n))).ToArray();
            firstData = line + 1;
            var number = 0;
            foreach (var cells in table.Rows)
            {
                line++;
                number++;
                var row = new StringBuilder(NumberXml(0, line, number, RowNumber));
                for (var c = 0; c < columns.Count; c++)
                {
                    row.Append(CellXml(c + 1, line, cells[c], columns[c].Kind, whole[c], false));
                    Width(c + 1, ReportText.Cell(cells[c], columns[c].Kind));
                }

                rows.Add(RowXml(line, 20, row.ToString()));
            }

            lastData = line;

            if (table.Total is { } total)
            {
                line++;
                var row = new StringBuilder(TextXml(0, line, "", TotalText));
                for (var c = 0; c < columns.Count; c++)
                {
                    row.Append(CellXml(c + 1, line, total[c], columns[c].Kind, whole[c], true));
                }

                rows.Add(RowXml(line, 24, row.ToString()));
            }

            if (table.Rows.Count == 0)
            {
                rows.Add(Wide(Note, "برای این دوره چیزی ثبت نشده است."));
            }

            if (!string.IsNullOrWhiteSpace(table.Note))
            {
                line++;
                rows.Add(Wide(Note, table.Note!));
            }
        }

        if (!string.IsNullOrWhiteSpace(document.Description))
        {
            line++;
            // Wrap the accounting basis so it stays readable in the saved workbook.
            rows.Add(Wide(Note, document.Description, Math.Max(44, Math.Ceiling(document.Description.Length / 95d) * 18)));
        }

        var cols = new StringBuilder("<cols>");
        widths[0] = Math.Max(widths[0], 5);
        for (var c = 0; c < widths.Length; c++)
        {
            cols.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Clamp(widths[c] * 1.15 + 3, 7, 60):0.##}\" customWidth=\"1\"/>");
        }

        cols.Append("</cols>");

        var range = headerLine > 0 ? $"$A${headerLine}:${Letter(columns.Count)}${Math.Max(lastData, headerLine)}" : null;
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        sheet.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
        sheet.Append("<sheetViews><sheetView rightToLeft=\"1\" showGridLines=\"0\" workbookViewId=\"0\">");
        if (headerLine > 0)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"<pane ySplit=\"{headerLine}\" topLeftCell=\"A{headerLine + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        }

        sheet.Append("</sheetView></sheetViews>");
        sheet.Append("<sheetFormatPr defaultRowHeight=\"20\"/>");
        sheet.Append(cols);
        sheet.Append("<sheetData>").AppendJoin("", rows).Append("</sheetData>");
        if (range is not null && lastData >= firstData && firstData > 0)
        {
            sheet.Append("<autoFilter ref=\"").Append(range.Replace("$", "")).Append("\"/>");
        }

        if (merges.Count > 0)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"<mergeCells count=\"{merges.Count}\">");
            foreach (var merge in merges)
            {
                sheet.Append("<mergeCell ref=\"").Append(merge).Append("\"/>");
            }

            sheet.Append("</mergeCells>");
        }

        sheet.Append("<printOptions horizontalCentered=\"1\"/>");
        sheet.Append("<pageMargins left=\"0.4\" right=\"0.4\" top=\"0.5\" bottom=\"0.5\" header=\"0.3\" footer=\"0.3\"/>");
        sheet.Append(CultureInfo.InvariantCulture, $"<pageSetup paperSize=\"9\" orientation=\"{(columns.Count > 6 ? "landscape" : "portrait")}\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
        sheet.Append("<headerFooter><oddFooter>&amp;Cصفحه &amp;P از &amp;N</oddFooter></headerFooter>");
        sheet.Append("</worksheet>");

        var names = new StringBuilder();
        if (range is not null && lastData >= firstData && firstData > 0)
        {
            names.Append("<definedNames>");
            names.Append("<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'گزارش'!").Append(range).Append("</definedName>");
            names.Append(CultureInfo.InvariantCulture, $"<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">'گزارش'!${headerLine}:${headerLine}</definedName>");
            names.Append("</definedNames>");
        }

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
            Add(zip, "docProps/core.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                $"<dc:title>{Escape(document.Title)}</dc:title><dc:creator>{Escape(hospital ?? "")}</dc:creator></cp:coreProperties>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<bookViews><workbookView/></bookViews><sheets><sheet name=\"گزارش\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" + names + "</workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add(zip, "xl/styles.xml", Styles);
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return stream.ToArray();
    }

    private static bool Whole(decimal value) => value == decimal.Truncate(value);

    private static string CellXml(int column, int line, object? value, string kind, bool whole, bool total)
    {
        var numeric = kind is ReportKind.Money or ReportKind.Count or ReportKind.Quantity;
        if (numeric && ReportText.NumberOf(value) is { } number)
        {
            return NumberXml(column, line, number, total ? (whole ? TotalInt : TotalDecimal) : (whole ? IntCell : DecimalCell));
        }

        return TextXml(column, line, ReportText.Cell(value, kind), total ? TotalText : TextCell);
    }

    private static string RowXml(int line, double? height, string cells) =>
        height is { } h
            ? string.Create(CultureInfo.InvariantCulture, $"<row r=\"{line}\" ht=\"{h}\" customHeight=\"1\">{cells}</row>")
            : $"<row r=\"{line}\">{cells}</row>";

    /// <summary>Text is always an inline string, so nothing anyone typed can run as a formula.</summary>
    private static string TextXml(int column, int line, string text, int style) =>
        $"<c r=\"{Letter(column)}{line}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>";

    private static string NumberXml(int column, int line, decimal value, int style) =>
        $"<c r=\"{Letter(column)}{line}\" s=\"{style}\"><v>{(value == 0 ? 0 : value).ToString("0.############", CultureInfo.InvariantCulture)}</v></c>";

    private static string Letter(int column)
    {
        var name = "";
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }

        return name;
    }

    /// <summary>XML-escaped, without the control characters XML cannot hold.</summary>
    private static string Escape(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\t' or '\n' or '\r' || ch >= ' ')
            {
                clean.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
            }
        }

        return clean.ToString();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static readonly string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"#,##0.00\"/></numFmts>" +
        "<fonts count=\"7\">" +
        $"<font><sz val=\"11\"/><color rgb=\"FF2B3445\"/><name val=\"{Font}\"/><family val=\"2\"/></font>" +
        $"<font><b/><sz val=\"16\"/><color rgb=\"FF1F2A44\"/><name val=\"{Font}\"/><family val=\"2\"/></font>" +
        $"<font><sz val=\"10\"/><color rgb=\"FF6B7487\"/><name val=\"{Font}\"/><family val=\"2\"/></font>" +
        $"<font><b/><sz val=\"11\"/><color rgb=\"FF1F2A44\"/><name val=\"{Font}\"/><family val=\"2\"/></font>" +
        $"<font><sz val=\"11\"/><color rgb=\"FF3A4256\"/><name val=\"{Font}\"/><family val=\"2\"/></font>" +
        $"<font><i/><sz val=\"10\"/><color rgb=\"FF6B7487\"/><name val=\"{Font}\"/><family val=\"2\"/></font>" +
        $"<font><b/><sz val=\"12\"/><color rgb=\"FF0B6E7A\"/><name val=\"{Font}\"/><family val=\"2\"/></font>" +
        "</fonts>" +
        "<fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8EEF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF4F7FB\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left style=\"thin\"><color rgb=\"FFDCE3EC\"/></left><right style=\"thin\"><color rgb=\"FFDCE3EC\"/></right>" +
        "<top style=\"thin\"><color rgb=\"FFDCE3EC\"/></top><bottom style=\"thin\"><color rgb=\"FFDCE3EC\"/></bottom><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"16\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        // 1 hospital, 2 title, 3 subtitle
        "<xf numFmtId=\"0\" fontId=\"6\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        // 4 header
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\" readingOrder=\"2\"/></xf>" +
        // 5 text, 6 whole number, 7 decimal number
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        // 8 total text, 9 total whole, 10 total decimal
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"3\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"3\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        // 11 figure name, 12 figure whole, 13 figure decimal
        "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"3\" fontId=\"3\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "<xf numFmtId=\"164\" fontId=\"3\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        // 14 note, 15 row number
        "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\" wrapText=\"1\" readingOrder=\"2\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}
