using System.Xml.Linq;

namespace FinOps.IntegrationTests;

/// <summary>Reads the SSRS report definitions in reports/SSRS for contract checks.</summary>
internal static class RdlFiles
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition";

    internal sealed record Dataset(string File, string Procedure, IReadOnlyList<string> Parameters);

    internal static string Directory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FinOpsEnterprise.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), "reports", "SSRS");
    }

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
