using System.Globalization;
using ClosedXML.Excel;
using FinOps.Application.Reports;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FinOps.Infrastructure.Reporting;

/// <summary>Shared cell formatting so PDF and Excel show identical values.</summary>
internal static class ReportFormat
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    internal static string Text(ReportColumn column, object? value) => value switch
    {
        null => "",
        decimal d when column.Kind == ColumnKind.Money => d.ToString("C2", Culture),
        decimal d when column.Kind == ColumnKind.Percent => d.ToString("0.0", Culture) + "%",
        DateTimeOffset t when column.Kind == ColumnKind.Date => t.ToString("yyyy-MM-dd", Culture),
        DateTimeOffset t => t.ToString("yyyy-MM-dd HH:mm", Culture),
        DateOnly d => d.ToString("yyyy-MM-dd", Culture),
        IFormattable f => f.ToString(null, Culture),
        _ => value.ToString() ?? ""
    };

    internal static bool RightAligned(ReportColumn column) =>
        column.Kind is ColumnKind.Money or ColumnKind.Integer or ColumnKind.Percent;

    internal static decimal Total(ReportTable report, ReportColumn column) =>
        report.Rows.Sum(r => r.TryGetValue(column.Key, out var v) ? Convert.ToDecimal(v ?? 0m, Culture) : 0m);

    internal static string Footer => "FinOps Enterprise · synthetic data only";
}

/// <summary>Landscape A4 PDF with a repeating table header, totals row and page numbers.</summary>
internal sealed class PdfReportRenderer : IReportRenderer
{
    static PdfReportRenderer() => QuestPDF.Settings.License = LicenseType.Community;

    public string Format => "pdf";
    public string ContentType => "application/pdf";
    public string Extension => "pdf";

    public byte[] Render(ReportTable report) => Document.Create(container => container.Page(page =>
    {
        page.Size(PageSizes.A4.Landscape());
        page.Margin(28);
        page.DefaultTextStyle(x => x.FontSize(8.5f).FontColor("#263b30"));

        page.Header().Column(header =>
        {
            header.Item().Text(report.Title).FontSize(16).SemiBold().FontColor("#20372e");
            header.Item().PaddingBottom(4).Text(report.Description).FontColor("#65736a");
            header.Item().Text(text =>
            {
                foreach (var p in report.Parameters) text.Span($"{p.Label}: {p.Value}    ");
                text.Span($"Scope: {report.Scope}    ");
                text.Span($"Generated {report.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC by {report.GeneratedBy}");
            });
            header.Item().PaddingVertical(6).LineHorizontal(0.75f).LineColor("#dfe4dc");
        });

        page.Content().Element(content =>
        {
            if (report.Rows.Count == 0)
            {
                content.PaddingTop(20).Text("No data for the selected parameters.").Italic();
                return;
            }

            content.Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    foreach (var column in report.Columns)
                    {
                        if (column.Key is "description") columns.RelativeColumn(3);
                        else if (column.Kind is ColumnKind.Text) columns.RelativeColumn(2);
                        else columns.RelativeColumn(1.3f);
                    }
                });

                table.Header(header =>
                {
                    foreach (var column in report.Columns)
                    {
                        var cell = header.Cell().Background("#eef3ec").Padding(4);
                        var text = (ReportFormat.RightAligned(column) ? cell.AlignRight() : cell).Text(column.Header);
                        text.SemiBold().FontSize(8);
                    }
                });

                var striped = false;
                foreach (var row in report.Rows)
                {
                    foreach (var column in report.Columns)
                    {
                        var cell = table.Cell().Background(striped ? "#fafbf8" : "#ffffff").BorderBottom(0.5f)
                            .BorderColor("#edf0e9").Padding(4);
                        (ReportFormat.RightAligned(column) ? cell.AlignRight() : cell)
                            .Text(ReportFormat.Text(column, row.GetValueOrDefault(column.Key)));
                    }
                    striped = !striped;
                }

                if (report.Columns.Any(c => c.Total))
                {
                    var first = true;
                    foreach (var column in report.Columns)
                    {
                        var cell = table.Cell().BorderTop(1).BorderColor("#20372e").Padding(4);
                        var label = column.Total ? ReportFormat.Text(column, ReportFormat.Total(report, column)) : first ? "Total" : "";
                        (ReportFormat.RightAligned(column) ? cell.AlignRight() : cell).Text(label).SemiBold();
                        first = false;
                    }
                }
            });
        });

        page.Footer().Row(row =>
        {
            row.RelativeItem().Text(ReportFormat.Footer).FontSize(7).FontColor("#748074");
            row.RelativeItem().AlignRight().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(7).FontColor("#748074"));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    })).GeneratePdf();
}

/// <summary>
/// Excel workbook with typed cells (numbers stay numbers), a frozen header, an autofilter and SUM totals.
/// Text is always written as a string value, never a formula, so user-entered text such as
/// "=HYPERLINK(...)" cannot become a live formula (no formula injection).
/// </summary>
internal sealed class ExcelReportRenderer : IReportRenderer
{
    public string Format => "xlsx";
    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string Extension => "xlsx";

    public byte[] Render(ReportTable report)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = report.Title;
        workbook.Properties.Author = "FinOps Enterprise";
        var sheet = workbook.Worksheets.Add(SheetName(report.Title));

        sheet.Cell(1, 1).SetValue(report.Title);
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        sheet.Cell(2, 1).SetValue(string.Join("   ", report.Parameters.Select(p => $"{p.Label}: {p.Value}")
            .Append($"Scope: {report.Scope}")
            .Append($"Generated {report.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC by {report.GeneratedBy}")));
        sheet.Cell(2, 1).Style.Font.SetFontColor(XLColor.Gray);

        const int headerRow = 4;
        for (var c = 0; c < report.Columns.Count; c++)
        {
            var cell = sheet.Cell(headerRow, c + 1);
            cell.SetValue(report.Columns[c].Header);
            cell.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#EEF3EC"));
        }

        var rowNumber = headerRow;
        foreach (var row in report.Rows)
        {
            rowNumber++;
            for (var c = 0; c < report.Columns.Count; c++)
            {
                var column = report.Columns[c];
                var cell = sheet.Cell(rowNumber, c + 1);
                switch (row.GetValueOrDefault(column.Key))
                {
                    case null: break;
                    case decimal d: cell.SetValue(d); break;
                    case int i: cell.SetValue(i); break;
                    case DateTimeOffset t: cell.SetValue(t.UtcDateTime); break;
                    case DateOnly d: cell.SetValue(d.ToDateTime(TimeOnly.MinValue)); break;
                    case var other: cell.SetValue(other.ToString()); break; // always a literal string
                }
                ApplyFormat(cell, column);
            }
        }

        var firstData = headerRow + 1;
        if (report.Rows.Count > 0 && report.Columns.Any(c => c.Total))
        {
            var totalRow = rowNumber + 1;
            sheet.Cell(totalRow, 1).SetValue("Total");
            for (var c = 0; c < report.Columns.Count; c++)
            {
                if (!report.Columns[c].Total) continue;
                var cell = sheet.Cell(totalRow, c + 1);
                var letter = XLHelper.GetColumnLetterFromNumber(c + 1);
                cell.FormulaA1 = $"SUM({letter}{firstData}:{letter}{rowNumber})";
                ApplyFormat(cell, report.Columns[c]);
            }
            sheet.Row(totalRow).Style.Font.SetBold();
            sheet.Row(totalRow).Style.Border.SetTopBorder(XLBorderStyleValues.Thin);
        }

        if (report.Rows.Count > 0) sheet.Range(headerRow, 1, rowNumber, report.Columns.Count).SetAutoFilter();
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns(1, report.Columns.Count).AdjustToContents(headerRow, Math.Max(headerRow, rowNumber + 1));
        foreach (var column in sheet.ColumnsUsed()) if (column.Width > 60) column.Width = 60;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void ApplyFormat(IXLCell cell, ReportColumn column)
    {
        var format = column.Kind switch
        {
            ColumnKind.Money => "$#,##0.00",
            ColumnKind.Percent => "0.0\"%\"",
            ColumnKind.Integer => "#,##0",
            ColumnKind.Date => "yyyy-mm-dd",
            ColumnKind.DateTime => "yyyy-mm-dd hh:mm",
            _ => null
        };
        if (format is not null) cell.Style.NumberFormat.Format = format;
        if (ReportFormat.RightAligned(column)) cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
    }

    private static string SheetName(string title)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var clean = new string(title.Where(ch => !invalid.Contains(ch)).ToArray());
        return clean.Length > 31 ? clean[..31] : clean;
    }
}
