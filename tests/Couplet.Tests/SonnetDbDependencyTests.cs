using System.Xml.Linq;
using Couplet.Infrastructure.SonnetDb;

namespace Couplet.Tests;

public sealed class SonnetDbDependencyTests
{
    [Fact]
    public void Probe_FixedPackage_ReportsGraphApiAndVersionedHandshake()
    {
        var probe = new SonnetDbCapabilityProbe();

        var report = probe.Probe();

        Assert.Equal("fixed_package", report.Mode);
        Assert.Equal("3.1.0", report.Requested);
        Assert.NotEqual("unknown", report.ResolvedCommit);
        Assert.Contains(report.ResolvedCommit, report.ResolvedVersion, StringComparison.Ordinal);
        Assert.True(report.GraphApiPresent);
        Assert.True(report.DeclaresTrimCompatible);
        Assert.True(report.DeclaresAotCompatible);
        Assert.Equal("couplet.sonnetdb_handshake.v1", report.HandshakeVersion);
        Assert.Equal("available", report.State);
        Assert.Equal("fixed_package_verified", report.Reason);
        Assert.Contains(report.Capabilities, capability =>
            capability.Id == "graph.native"
            && capability.IntegrationState == "available"
            && capability.ReleaseLevel == "unavailable"
            && capability.BlockingGaps.Contains("CG-001"));
    }

    [Fact]
    public void ProjectGraph_SonnetDbReference_IsOneWayAndPackagePinned()
    {
        string root = FindRepositoryRoot();
        string[] projectFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);

        var sonnetReferences = projectFiles
            .SelectMany(project => ReadIncludes(project, "PackageReference")
                .Where(include => string.Equals(include, "SonnetDB.Core", StringComparison.OrdinalIgnoreCase))
                .Select(include => (Project: project, Include: include)))
            .ToArray();

        Assert.Single(sonnetReferences);
        Assert.EndsWith(
            Path.Combine("src", "Couplet.Infrastructure.SonnetDb", "Couplet.Infrastructure.SonnetDb.csproj"),
            sonnetReferences[0].Project,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SonnetDB.Core", sonnetReferences[0].Include);

        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.DoesNotContain("repository: IoTSharp/SonnetDB", workflow, StringComparison.Ordinal);

        XDocument packages = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        Assert.Equal("3.1.0", PackageVersion(packages, "SonnetDB.Core"));
    }

    [Fact]
    public void BuildPolicy_ProductionCode_RequiresTrimAotAndSourceGeneratedJson()
    {
        string root = FindRepositoryRoot();
        XDocument buildPolicy = XDocument.Load(Path.Combine(root, "Directory.Build.props"));

        Assert.Equal("enable", PropertyValue(buildPolicy, "Nullable"));
        Assert.Equal("enable", PropertyValue(buildPolicy, "ImplicitUsings"));
        Assert.Equal("true", PropertyValue(buildPolicy, "TreatWarningsAsErrors"));
        Assert.Equal("true", PropertyValue(buildPolicy, "IsTrimmable"));
        Assert.Equal("true", PropertyValue(buildPolicy, "IsAotCompatible"));
        Assert.Equal("false", PropertyValue(buildPolicy, "JsonSerializerIsReflectionEnabledByDefault"));
        Assert.EndsWith(
            Path.Combine("artifacts", "nuget"),
            PropertyValue(buildPolicy, "RestorePackagesPath").Replace("$(MSBuildThisFileDirectory)", root + Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        string[] sourceFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        Assert.DoesNotContain(sourceFiles, file => File.ReadAllText(file).Contains("unsafe", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ReadIncludes(string projectPath, string itemName) =>
        XDocument.Load(projectPath)
            .Descendants(itemName)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => include is not null)
            .Select(include => include!);

    private static string PropertyValue(XDocument document, string name) =>
        document.Descendants(name).Last().Value;

    private static string PackageVersion(XDocument document, string packageId) =>
        document.Descendants("PackageVersion")
            .Single(element => string.Equals(
                element.Attribute("Include")?.Value,
                packageId,
                StringComparison.Ordinal))
            .Attribute("Version")?.Value
        ?? throw new InvalidOperationException($"Package version for '{packageId}' was not found.");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Couplet.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the Couplet repository root.");
    }
}
