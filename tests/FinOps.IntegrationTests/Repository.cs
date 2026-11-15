using System.Reflection;

namespace FinOps.IntegrationTests;

/// <summary>The repository checkout these tests were built from (recorded at build time by the project file).</summary>
internal static class Repository
{
    internal static string Root { get; } = Find();

    private static string Find()
    {
        var recorded = typeof(Repository).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryRoot")?.Value;
        if (recorded is not null && File.Exists(Path.Combine(recorded, "FinOpsEnterprise.slnx"))) return recorded;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FinOpsEnterprise.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
