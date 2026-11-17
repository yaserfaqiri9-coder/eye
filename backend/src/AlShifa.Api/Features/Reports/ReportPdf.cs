using System.Reflection;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AlShifa.Api.Features.Reports;

/// <summary>
/// A report as an A4 PDF, right to left in Vazirmatn: the hospital, the report's name and days on every page, its figures
/// in boxes, then its list with a repeated header, a row number and a bold total, and the page number at the foot.
/// </summary>
public static class ReportPdf
{
    private const string Family = "Vazirmatn";
    private const string Ink = "#1F2A44";
    private const string Soft = "#6B7487";
    private const string Body = "#2B3445";
    private const string Accent = "#0B6E7A";
    private const string HeaderFill = "#E8EEF7";
    private const string TotalFill = "#F4F7FB";
    private const string Line = "#DCE3EC";

    private static readonly Lazy<bool> Ready = new(() =>
    {
        // Free under the Community licence for organisations with under 1M USD yearly revenue.
        QuestPDF.Settings.License = LicenseType.Community;
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "ReportFonts.Vazirmatn-Regular.ttf", "ReportFonts.Vazirmatn-Bold.ttf" })
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing font {name}.");
            FontManager.RegisterFontFromStream(stream);
        }

        return true;
    });

    public static byte[] Build(ReportDocument document, string? hospital) => Create(document, hospital).GeneratePdf();

    /// <summary>The document itself, so it can also be drawn as page images to check its look.</summary>
    public static IDocument Create(ReportDocument document, string? hospital)
    {
        _ = Ready.Value;
        var table = document.Tables.FirstOrDefault();
        var columns = table?.Columns ?? [];
        var printedAt = ReportText.Time(DateTime.UtcNow);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(columns.Count > 6 ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(1.2f, Unit.Centimetre);
            page.ContentFromRightToLeft();
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => style.FontFamily(Family).FontSize(9.5f).FontColor(Body));

            page.Header().PaddingBottom(10).Column(head =>
            {
                head.Item().Row(row =>
                {
                    row.RelativeItem().Column(name =>
                    {
                        if (!string.IsNullOrWhiteSpace(hospital))
                        {
                            name.Item().Text(hospital).Bold().FontSize(11).FontColor(Accent);
                        }

                        name.Item().Text(document.Title).Bold().FontSize(17).FontColor(Ink);
                    });
                    row.AutoItem().AlignBottom().Column(meta =>
                    {
                        meta.Item().AlignLeft().Text(ReportText.Period(document.From, document.To)).FontSize(10).SemiBold().FontColor(Ink);
                        meta.Item().AlignLeft().Text($"گرفته‌شده در {printedAt}").FontSize(8.5f).FontColor(Soft);
                    });
                });
                head.Item().PaddingTop(8).LineHorizontal(1.2f).LineColor(Accent);
            });

            page.Content().Column(body =>
            {
                body.Spacing(12);

                if (document.Tiles.Count > 0)
                {
                    body.Item().Row(row =>
                    {
                        row.Spacing(8);
                        foreach (var tile in document.Tiles)
                        {
                            row.RelativeItem().Border(0.8f).BorderColor(Line).Background(TotalFill).PaddingVertical(7).PaddingHorizontal(10).Column(box =>
                            {
                                box.Item().Text(tile.Label).FontSize(8.5f).FontColor(Soft);
                                box.Item().Text(text =>
                                {
                                    text.Span(ReportText.Number(tile.Value)).Bold().FontSize(14).FontColor(Ink);
                                    if (tile.Kind == ReportKind.Money)
                                    {
                                        text.Span("  افغانی").FontSize(8).FontColor(Soft);
                                    }
                                });
                            });
                        }
                    });
                }

                if (table is null)
                {
                    return;
                }

                body.Item().Text(table.Title).Bold().FontSize(11.5f).FontColor(Ink);

                if (table.Rows.Count == 0)
                {
                    body.Item().Border(0.8f).BorderColor(Line).Padding(14).AlignCenter().Text("برای این دوره چیزی ثبت نشده است.").FontColor(Soft);
                }
                else
                {
                    body.Item().Table(grid =>
                    {
                        grid.ColumnsDefinition(define =>
                        {
                            define.ConstantColumn(34);
                            foreach (var weight in Weights(table))
                            {
                                define.RelativeColumn(weight);
                            }
                        });

                        grid.Header(header =>
                        {
                            header.Cell().Element(HeaderCell).AlignCenter().Text("ردیف");
                            foreach (var column in columns)
                            {
                                header.Cell().Element(HeaderCell).AlignCenter().Text(column.Kind == ReportKind.Money ? $"{column.Label} (افغانی)" : column.Label);
                            }
                        });

                        var number = 0;
                        foreach (var cells in table.Rows)
                        {
                            number++;
                            grid.Cell().Element(BodyCell).AlignCenter().Text(ReportText.Number(number)).FontColor(Soft).FontSize(8.5f);
                            for (var c = 0; c < columns.Count; c++)
                            {
                                Value(grid.Cell().Element(BodyCell), cells[c], columns[c].Kind, false);
                            }
                        }

                        if (table.Total is { } total)
                        {
                            grid.Cell().Element(TotalCell).Text("");
                            for (var c = 0; c < columns.Count; c++)
                            {
                                Value(grid.Cell().Element(TotalCell), total[c], columns[c].Kind, true);
                            }
                        }
                    });
                }

                if (!string.IsNullOrWhiteSpace(document.Description))
                {
                    body.Item().Text(document.Description).FontSize(8.5f).FontColor(Soft);
                }

                if (!string.IsNullOrWhiteSpace(table.Note))
                {
                    body.Item().Text(table.Note).Italic().FontSize(8.5f).FontColor(Soft);
                }
            });

            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8.5f).FontColor(Soft));
                text.Span("صفحه ");
                text.CurrentPageNumber();
                text.Span(" از ");
                text.TotalPages();
            });
        }));
    }

    private static IContainer HeaderCell(IContainer cell) =>
        cell.Background(HeaderFill).Border(0.6f).BorderColor(Line).PaddingVertical(6).PaddingHorizontal(5).DefaultTextStyle(style => style.Bold().FontSize(9).FontColor(Ink));

    private static IContainer BodyCell(IContainer cell) =>
        cell.BorderBottom(0.6f).BorderColor(Line).PaddingVertical(3.5f).PaddingHorizontal(5);

    private static IContainer TotalCell(IContainer cell) =>
        cell.Background(TotalFill).BorderTop(1f).BorderBottom(0.6f).BorderColor("#C9D3E1").PaddingVertical(6).PaddingHorizontal(5).DefaultTextStyle(style => style.Bold().FontColor(Ink));

    /// <summary>Numbers sit in the middle of their column; text starts at the right like the reading does.</summary>
    private static void Value(IContainer cell, object? value, string kind, bool total)
    {
        var text = ReportText.Cell(value, kind);
        var numeric = kind is ReportKind.Money or ReportKind.Count or ReportKind.Quantity && ReportText.NumberOf(value) is not null;
        var negative = numeric && ReportText.NumberOf(value) < 0;
        // Text keeps the page's right-to-left start, so it needs no alignment of its own.
        var dated = kind is ReportKind.Date or ReportKind.DateTime && value is not string;
        var span = (numeric || dated ? cell.AlignCenter() : cell).Text(text);
        if (negative)
        {
            span.FontColor("#C0392B");
        }

        if (total && text.Length > 0 && !numeric)
        {
            span.Bold();
        }
    }

    /// <summary>A column is as wide as its longest text needs, within limits, so a name never squeezes a number.</summary>
    private static IEnumerable<float> Weights(ReportTable table) =>
        table.Columns.Select((column, c) =>
        {
            var longest = table.Rows.Take(300).Select(r => ReportText.Cell(r[c], column.Kind).Length).DefaultIfEmpty(0).Max();
            return (float)Math.Clamp(Math.Max(longest, column.Label.Length * 0.8), 4, 30);
        });
}
