"""Generates the FinOps Enterprise SSRS report definitions (RDL 2016) from one table of definitions.

Run from the repository root:  python reports/SSRS/tools/generate_rdl.py

The reports call the same stored procedures as the API. Department isolation does NOT come from these
files: @DepartmentIds is a hidden parameter that defaults to "[]" (no departments), so a report run directly
on a report server returns nothing unless the caller supplies a scope. See
docs/decisions/0004-report-access.md for how a deployment must set it.
"""
import math
import pathlib
import uuid
from xml.sax.saxutils import escape

OUT = pathlib.Path(__file__).resolve().parents[1]
NS_ID = uuid.UUID("8f3c2a4e-6b1d-4f7a-9c2e-1a5b7d9e0f11")  # stable IDs: regenerating yields identical files


def sid(*parts):
    return str(uuid.uuid5(NS_ID, "/".join(parts)))


# Parameter: (name, rdl data type, prompt, default expression, hidden, nullable)
FROM = ("From", "DateTime", "From", "=DateSerial(Year(Today()), Month(Today()), 1)", False, False)
TO = ("To", "DateTime", "To", "=Today()", False, False)
SCOPE = ("DepartmentIds", "String", "Department scope (set by the API)", "[]", True, True)
MAXROWS = ("MaxRows", "Integer", "Maximum rows", "5000", True, False)

# Column: (header, value expression, format or None, width inches, total?)
REPORTS = [
    {
        "file": "ExpenseSummary", "title": "Department expense summary",
        "procedure": "finance.usp_ExpenseSummary", "parameters": [FROM, TO, SCOPE],
        "fields": [("DepartmentName", "System.String"), ("Category", "System.String"), ("ExpenseCount", "System.Int32"),
                   ("Posted", "System.Decimal"), ("Approved", "System.Decimal"), ("Pending", "System.Decimal"),
                   ("Total", "System.Decimal")],
        "columns": [("Department", "=Fields!DepartmentName.Value", None, 2.0, False),
                    ("Category", "=Fields!Category.Value", None, 1.4, False),
                    ("Expenses", "=Fields!ExpenseCount.Value", "#,##0", 0.9, True),
                    ("Posted", "=Fields!Posted.Value", "C2", 1.2, True),
                    ("Approved", "=Fields!Approved.Value", "C2", 1.2, True),
                    ("Pending", "=Fields!Pending.Value", "C2", 1.2, True),
                    ("Total", "=Fields!Total.Value", "C2", 1.3, True)],
    },
    {
        "file": "BudgetVsActual", "title": "Budget vs. actual",
        "procedure": "finance.usp_BudgetVsActual",
        "parameters": [("Year", "Integer", "Year", "=Year(Today())", False, False),
                       ("Month", "Integer", "Month (blank = whole year)", None, False, True), SCOPE],
        "fields": [("DepartmentName", "System.String"), ("Month", "System.Int32"), ("Budget", "System.Decimal"),
                   ("Posted", "System.Decimal"), ("Committed", "System.Decimal"), ("Pending", "System.Decimal"),
                   ("Remaining", "System.Decimal"), ("UtilizationPercent", "System.Decimal")],
        "columns": [("Department", "=Fields!DepartmentName.Value", None, 1.8, False),
                    ("Month", "=MonthName(Fields!Month.Value, True)", None, 0.7, False),
                    ("Budget", "=Fields!Budget.Value", "C2", 1.1, True),
                    ("Spent", "=Fields!Posted.Value", "C2", 1.1, True),
                    ("Committed", "=Fields!Committed.Value", "C2", 1.1, True),
                    ("Pending", "=Fields!Pending.Value", "C2", 1.1, True),
                    ("Remaining", "=Fields!Remaining.Value", "C2", 1.1, True),
                    ("Used %", "=Fields!UtilizationPercent.Value", "0.0", 0.8, False)],
    },
    {
        "file": "ApprovalAging", "title": "Pending approval aging",
        "procedure": "finance.usp_ApprovalAging",
        "parameters": [("AsOfUtc", "DateTime", "As of (UTC)", "=Now().ToUniversalTime()", True, False), SCOPE],
        "fields": [("DepartmentName", "System.String"), ("RequesterName", "System.String"), ("Description", "System.String"),
                   ("Amount", "System.Decimal"), ("SubmittedAtUtc", "System.DateTimeOffset"), ("AgeDays", "System.Int32"),
                   ("AgeBucket", "System.String")],
        "columns": [("Department", "=Fields!DepartmentName.Value", None, 1.5, False),
                    ("Requester", "=Fields!RequesterName.Value", None, 1.4, False),
                    ("Description", "=Fields!Description.Value", None, 2.6, False),
                    ("Amount", "=Fields!Amount.Value", "C2", 1.0, True),
                    ("Submitted (UTC)", "=Fields!SubmittedAtUtc.Value.UtcDateTime", "yyyy-MM-dd HH:mm", 1.3, False),
                    ("Age (days)", "=Fields!AgeDays.Value", "#,##0", 0.8, False),
                    ("Age band", "=Fields!AgeBucket.Value", None, 1.0, False)],
    },
    {
        "file": "TransactionLedger", "title": "Transaction ledger",
        "procedure": "finance.usp_TransactionLedger", "parameters": [FROM, TO, SCOPE, MAXROWS],
        "fields": [("PostedAtUtc", "System.DateTimeOffset"), ("DepartmentName", "System.String"),
                   ("RequesterName", "System.String"), ("Category", "System.String"), ("Description", "System.String"),
                   ("Amount", "System.Decimal"), ("PostedByName", "System.String")],
        "columns": [("Posted (UTC)", "=Fields!PostedAtUtc.Value.UtcDateTime", "yyyy-MM-dd HH:mm", 1.3, False),
                    ("Department", "=Fields!DepartmentName.Value", None, 1.4, False),
                    ("Requester", "=Fields!RequesterName.Value", None, 1.3, False),
                    ("Category", "=Fields!Category.Value", None, 0.9, False),
                    ("Description", "=Fields!Description.Value", None, 2.3, False),
                    ("Amount", "=Fields!Amount.Value", "C2", 1.0, True),
                    ("Posted by", "=Fields!PostedByName.Value", None, 1.3, False)],
    },
    {
        "file": "AuditActivity", "title": "Audit activity (restricted)",
        "procedure": "audit.usp_AuditActivity", "parameters": [FROM, TO, MAXROWS],
        "fields": [("OccurredAtUtc", "System.DateTimeOffset"), ("ActorName", "System.String"), ("Action", "System.String"),
                   ("ResourceType", "System.String"), ("Outcome", "System.String"), ("CorrelationId", "System.String")],
        "columns": [("Time (UTC)", "=Fields!OccurredAtUtc.Value.UtcDateTime", "yyyy-MM-dd HH:mm:ss", 1.5, False),
                    ("Actor", "=Fields!ActorName.Value", None, 1.6, False),
                    ("Action", "=Fields!Action.Value", None, 1.6, False),
                    ("Resource", "=Fields!ResourceType.Value", None, 1.0, False),
                    ("Outcome", "=Fields!Outcome.Value", None, 0.9, False),
                    ("Correlation ID", "=Fields!CorrelationId.Value", None, 3.0, False)],
    },
]


def textbox(name, value, *, bold=False, align=None, fmt=None, background=None, size=None, border_bottom=None):
    style = []
    if fmt: style.append(f"<Format>{escape(fmt)}</Format>")
    if bold: style.append("<FontWeight>Bold</FontWeight>")
    if size: style.append(f"<FontSize>{size}</FontSize>")
    run_style = "".join(style)
    box_style = ["<PaddingLeft>3pt</PaddingLeft>", "<PaddingRight>3pt</PaddingRight>",
                 "<PaddingTop>2pt</PaddingTop>", "<PaddingBottom>2pt</PaddingBottom>"]
    if background: box_style.insert(0, f"<BackgroundColor>{background}</BackgroundColor>")
    if border_bottom: box_style.insert(0, f"<BottomBorder><Color>{border_bottom}</Color><Style>Solid</Style><Width>0.5pt</Width></BottomBorder>")
    para_style = f"<Style><TextAlign>{align}</TextAlign></Style>" if align else "<Style />"
    return (f'<Textbox Name="{name}"><CanGrow>true</CanGrow><KeepTogether>true</KeepTogether>'
            f"<Paragraphs><Paragraph><TextRuns><TextRun><Value>{escape(value)}</Value>"
            f"<Style>{run_style}</Style></TextRun></TextRuns>{para_style}</Paragraph></Paragraphs>"
            f"<Style>{''.join(box_style)}</Style></Textbox>")


def report(r):
    numeric = lambda fmt: fmt is not None and not fmt.startswith("yyyy")
    cols = r["columns"]
    width = sum(c[3] for c in cols)
    header = "".join(
        f"<TablixCell><CellContents>{textbox(f'H{i}', h, bold=True, background='#EEF3EC', align='Right' if numeric(f) else None)}</CellContents></TablixCell>"
        for i, (h, _, f, _, _) in enumerate(cols))
    detail = "".join(
        f"<TablixCell><CellContents>{textbox(f'D{i}', v, fmt=f, align='Right' if numeric(f) else None, border_bottom='#EDF0E9')}</CellContents></TablixCell>"
        for i, (_, v, f, _, _) in enumerate(cols))
    total_cells = []
    for i, (_, v, f, _, total) in enumerate(cols):
        value = f"=Sum({v[1:]})" if total else ("Total" if i == 0 else "")
        total_cells.append(f"<TablixCell><CellContents>{textbox(f'T{i}', value, bold=True, fmt=f if total else None, align='Right' if total else None)}</CellContents></TablixCell>")
    has_totals = any(c[4] for c in cols)

    rows = f"<TablixRow><Height>0.3in</Height><TablixCells>{header}</TablixCells></TablixRow>"
    rows += f"<TablixRow><Height>0.25in</Height><TablixCells>{detail}</TablixCells></TablixRow>"
    members = ("<TablixMember><KeepWithGroup>After</KeepWithGroup><RepeatOnNewPage>true</RepeatOnNewPage></TablixMember>"
               '<TablixMember><Group Name="Details" /></TablixMember>')
    if has_totals:
        rows += f"<TablixRow><Height>0.3in</Height><TablixCells>{''.join(total_cells)}</TablixCells></TablixRow>"
        members += "<TablixMember><KeepWithGroup>Before</KeepWithGroup></TablixMember>"

    tablix = (f'<Tablix Name="Table"><TablixBody><TablixColumns>'
              + "".join(f"<TablixColumn><Width>{c[3]}in</Width></TablixColumn>" for c in cols)
              + f"</TablixColumns><TablixRows>{rows}</TablixRows></TablixBody>"
              + "<TablixColumnHierarchy><TablixMembers>" + "<TablixMember />" * len(cols) + "</TablixMembers></TablixColumnHierarchy>"
              + f"<TablixRowHierarchy><TablixMembers>{members}</TablixMembers></TablixRowHierarchy>"
              + "<NoRowsMessage>No data for the selected parameters.</NoRowsMessage>"
              + f"<DataSetName>Main</DataSetName><Top>0.75in</Top><Height>{0.85 if has_totals else 0.55}in</Height>"
              + f"<Width>{width:.2f}in</Width><Style><Border><Style>None</Style></Border></Style></Tablix>")

    title = textbox("Title", r["title"], bold=True, size="16pt").replace("<Textbox Name=\"Title\">",
                                                                          '<Textbox Name="Title">', 1)
    title = title.replace("</Textbox>", f"<Top>0in</Top><Height>0.4in</Height><Width>{width:.2f}in</Width></Textbox>")
    subtitle_value = '="Generated " & Format(Globals!ExecutionTime, "yyyy-MM-dd HH:mm") & " · synthetic data only"'
    subtitle = textbox("Subtitle", subtitle_value).replace(
        "</Textbox>", f"<Top>0.4in</Top><Height>0.25in</Height><Width>{width:.2f}in</Width></Textbox>")

    query_params = "".join(
        f'<QueryParameter Name="@{p[0]}"><Value>=Parameters!{p[0]}.Value</Value></QueryParameter>' for p in r["parameters"])
    fields = "".join(
        f'<Field Name="{n}"><DataField>{n}</DataField><rd:TypeName>{t}</rd:TypeName></Field>' for n, t in r["fields"])

    params_xml = ""
    for name, dtype, prompt, default, hidden, nullable in r["parameters"]:
        params_xml += f'<ReportParameter Name="{name}"><DataType>{dtype}</DataType>'
        if nullable: params_xml += "<Nullable>true</Nullable>"
        if default is not None:
            params_xml += f"<DefaultValue><Values><Value>{escape(default)}</Value></Values></DefaultValue>"
        elif nullable:
            params_xml += '<DefaultValue><Values><Value xsi:nil="true" /></Values></DefaultValue>'
        params_xml += f"<Prompt>{escape(prompt)}</Prompt>"
        if hidden: params_xml += "<Hidden>true</Hidden>"
        params_xml += "</ReportParameter>"

    n = len(r["parameters"])
    cells = "".join(
        f"<CellDefinition><ColumnIndex>{i % 4}</ColumnIndex><RowIndex>{i // 4}</RowIndex><ParameterName>{p[0]}</ParameterName></CellDefinition>"
        for i, p in enumerate(r["parameters"]))
    layout = (f"<ReportParametersLayout><GridLayoutDefinition><NumberOfColumns>4</NumberOfColumns>"
              f"<NumberOfRows>{max(1, math.ceil(n / 4))}</NumberOfRows><CellDefinitions>{cells}</CellDefinitions>"
              f"</GridLayoutDefinition></ReportParametersLayout>")

    footer = ('<PageFooter><Height>0.3in</Height><PrintOnFirstPage>true</PrintOnFirstPage><PrintOnLastPage>true</PrintOnLastPage>'
              "<ReportItems>"
              + textbox("PageNumber", '="Page " & Globals!PageNumber & " of " & Globals!TotalPages', align="Right").replace(
                  "</Textbox>", f"<Top>0in</Top><Height>0.25in</Height><Width>{width:.2f}in</Width></Textbox>")
              + "</ReportItems><Style /></PageFooter>")

    return ('<?xml version="1.0" encoding="utf-8"?>\n'
            '<Report xmlns="http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition" '
            'xmlns:rd="http://schemas.microsoft.com/SQLServer/reporting/reportdesigner" '
            'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
            f"  <!-- Generated by reports/SSRS/tools/generate_rdl.py. Edit the generator, not this file. -->\n"
            "  <AutoRefresh>0</AutoRefresh>\n"
            f'  <DataSources><DataSource Name="FinOps"><DataSourceReference>FinOps</DataSourceReference>'
            f"<rd:DataSourceID>{sid('datasource')}</rd:DataSourceID></DataSource></DataSources>\n"
            f'  <DataSets><DataSet Name="Main"><Query><DataSourceName>FinOps</DataSourceName>'
            f"<QueryParameters>{query_params}</QueryParameters><CommandType>StoredProcedure</CommandType>"
            f"<CommandText>{r['procedure']}</CommandText></Query><Fields>{fields}</Fields></DataSet></DataSets>\n"
            f"  <ReportSections><ReportSection><Body><ReportItems>{title}{subtitle}{tablix}</ReportItems>"
            f"<Height>2in</Height><Style /></Body><Width>{width:.2f}in</Width>"
            f"<Page>{footer}<PageHeight>8.5in</PageHeight><PageWidth>11in</PageWidth>"
            "<LeftMargin>0.4in</LeftMargin><RightMargin>0.4in</RightMargin><TopMargin>0.4in</TopMargin>"
            "<BottomMargin>0.4in</BottomMargin><Style /></Page></ReportSection></ReportSections>\n"
            f"  <ReportParameters>{params_xml}</ReportParameters>\n"
            f"  {layout}\n"
            "  <rd:ReportUnitType>Inch</rd:ReportUnitType>\n"
            f"  <rd:ReportID>{sid('report', r['file'])}</rd:ReportID>\n"
            "</Report>\n")


def data_source():
    return ('<?xml version="1.0" encoding="utf-8"?>\n'
            '<!-- Shared data source. Credentials are configured on the report server, never stored here. -->\n'
            '<RptDataSource xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
            'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" Name="FinOps">\n'
            "  <ConnectionProperties>\n"
            "    <Extension>SQL</Extension>\n"
            "    <ConnectString>Data Source=localhost,14333;Initial Catalog=FinOpsEnterprise;Encrypt=True;TrustServerCertificate=True</ConnectString>\n"
            "  </ConnectionProperties>\n"
            f"  <DataSourceID>{sid('datasource')}</DataSourceID>\n"
            "</RptDataSource>\n")


if __name__ == "__main__":
    for r in REPORTS:
        (OUT / f"{r['file']}.rdl").write_text(report(r), encoding="utf-8", newline="\n")
    (OUT / "FinOps.rds").write_text(data_source(), encoding="utf-8", newline="\n")
    print(f"Wrote {len(REPORTS)} reports and FinOps.rds to {OUT}")
