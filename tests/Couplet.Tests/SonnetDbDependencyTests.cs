using System.Xml.Linq;
using Couplet.Infrastructure.SonnetDb;

namespace Couplet.Tests;

public sealed class SonnetDbDependencyTests
{
    private const string _expectedCommit = "a0fefe15c4ea4d3a5f2a4a2c4f69d6930b9c6c70";

    [Fact]
    public void Probe_CurrentSourceDependency_ReportsGraphApiButUnavailableHandshake()
    {
        var probe = new SonnetDbCapabilityProbe();

        var report = probe.Probe();

        Assert.Equal("source_project_reference", report.Mode);
        Assert.Equal(_expectedCommit, report.ResolvedCommit);
        Assert.Contains(_expectedCommit, report.ResolvedVersion, StringComparison.Ordinal);
        Assert.True(report.GraphApiPresent);
        Assert.True(report.DeclaresTrimCompatible);
        Assert.True(report.DeclaresAotCompatible);
        Assert.Equal("unavailable", report.State);
        Assert.Equal("sonnetdb_capability_handshake_not_implemented", report.Reason);
    }

    [Fact]
    public void ProjectGraph_SonnetDbReference_IsOneWayAndSourcePinned()
    {
        string root = FindRepositoryRoot();
        string[] projectFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);

        var sonnetReferences = projectFiles
            .SelectMany(project => ReadIncludes(project, "ProjectReference")
                .Where(include => include.Contains("SonnetDbSourceProject", StringComparison.Ordinal))
                .Select(include => (Project: project, Include: include)))
            .ToArray();

        Assert.Single(sonnetReferences);
        Assert.EndsWith(
            Path.Combine("src", "Couplet.Infrastructure.SonnetDb", "Couplet.Infrastructure.SonnetDb.csproj"),
            sonnetReferences[0].Project,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("$(SonnetDbSourceProject)", sonnetReferences[0].Include);
        Assert.DoesNotContain(
            projectFiles.SelectMany(project => ReadIncludes(project, "PackageReference")),
            include => string.Equals(include, "SonnetDB.Core", StringComparison.OrdinalIgnoreCase));

        XDocument pinDocument = XDocument.Load(Path.Combine(root, "eng", "SonnetDB.props"));
        string? pinnedCommit = pinDocument.Descendants("SonnetDbSourceCommit").Single().Value;
        Assert.Equal(_expectedCommit, pinnedCommit);

        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Contains(_expectedCommit, workflow, StringComparison.Ordinal);

        XDocument packages = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        Assert.Equal("10.0.10", PackageVersion(packages, "System.IO.Hashing"));
        Assert.Equal("10.0.10", PackageVersion(packages, "System.Numerics.Tensors"));
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
