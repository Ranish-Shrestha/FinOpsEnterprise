using System.Xml.Linq;

namespace FinOps.IntegrationTests;

/// <summary>Reads the SSRS report definitions in reports/SSRS for contract checks.</summary>
internal static class RdlFiles
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition";

    internal sealed record Dataset(string File, string Procedure, IReadOnlyList<string> Parameters);

    internal static string Directory() => Path.Combine(Repository.Root, "reports", "SSRS");

    internal static IEnumerable<string> Paths() => System.IO.Directory.GetFiles(Directory(), "*.rdl").Order();

    internal static XDocument Load(string path) => XDocument.Load(path);

    internal static IEnumerable<Dataset> Datasets() =>
        from path in Paths()
        let doc = Load(path)
        from query in doc.Descendants(Rdl + "Query")
        where (string?)query.Element(Rdl + "CommandType") == "StoredProcedure"
        select new Dataset(
            Path.GetFileName(path),
            (string)query.Element(Rdl + "CommandText")!,
            query.Element(Rdl + "QueryParameters")?.Elements(Rdl + "QueryParameter")
                .Select(p => (string)p.Attribute("Name")!).ToList() ?? []);
}
