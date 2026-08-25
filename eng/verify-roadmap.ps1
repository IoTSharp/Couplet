[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

$requiredFiles = @(
    '.editorconfig',
    '.gitattributes',
    '.gitignore',
    '.github/workflows/ci.yml',
    '.github/workflows/docs.yml',
    'Couplet.slnx',
    'Directory.Build.props',
    'Directory.Packages.props',
    'global.json',
    'README.md',
    'ROADMAP.md',
    'AGENTS.md',
    'CHANGELOG.md',
    'contracts/c0-handshake.v1.json',
    'contracts/code-graph/v1/schema.json',
    'contracts/indexing/v1/schema.json',
    'contracts/mcp/v1/schema-catalog.json',
    'contracts/security/v1/policy.schema.json',
    'fixtures/c0/manifest.v1.json',
    'fixtures/c0/golden-answers.v1.json',
    'fixtures/c0/agent-eval-manifest.v1.json',
    'fixtures/c1/capacity-manifest.v1.json',
    'docs/architecture.md',
    'docs/code-graph-v1-contract.md',
    'docs/security-and-data-lifecycle.md',
    'docs/c0-evidence.md',
    'docs/c1-indexing-evidence.md',
    'docs/c1-capacity-evidence.md',
    'docs/mcp-v1-contract.md',
    'docs/golden-journeys.md',
    'docs/quality-gates.md',
    'docs/capability-gaps.md',
    'docs/cpl-007-foundation.md',
    'docs/sonnetdb-capability-matrix.md',
    'docs/adr/0001-product-and-repository-boundary.md',
    'docs/adr/0002-native-property-graph-no-bypass.md',
    'docs/adr/0003-performance-gaps-block-release.md',
    'docs/adr/0004-dotnet-host-and-source-dependency.md'
)

$errors = [System.Collections.Generic.List[string]]::new()

foreach ($relativePath in $requiredFiles)
{
    $fullPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf))
    {
        $errors.Add("Missing required file: $relativePath")
    }
}

$requiredRoadmapTokens = @(
    'C0：基础与合同',
    'C1：增量代码索引',
    'C2：原生图代码智能',
    'C3：混合检索与 Context Pack',
    'C4：生产与 Agent 体验',
    'Couplet -> SonnetDB.Core',
    'https://github.com/IoTSharp/Couplet',
    '#352',
    '#359',
    '#367'
)

$roadmapPath = Join-Path $repositoryRoot 'ROADMAP.md'
if (Test-Path -LiteralPath $roadmapPath -PathType Leaf)
{
    $roadmap = Get-Content -LiteralPath $roadmapPath -Raw
    foreach ($token in $requiredRoadmapTokens)
    {
        if (-not $roadmap.Contains($token, [StringComparison]::Ordinal))
        {
            $errors.Add("ROADMAP.md is missing required token: $token")
        }
    }
}

$markdownFiles = Get-ChildItem -LiteralPath $repositoryRoot -Filter '*.md' -File -Recurse |
    Where-Object {
        $_.FullName -notmatch '[\\/](\.git|artifacts|bin|obj)[\\/]'
    }
$linkPattern = [regex]'\[[^\]]+\]\((?<target>[^)]+)\)'

foreach ($markdownFile in $markdownFiles)
{
    $content = [System.IO.File]::ReadAllText($markdownFile.FullName)
    if ($content.Length -gt 0 -and -not $content.EndsWith("`n", [StringComparison]::Ordinal))
    {
        $errors.Add("File must end with a newline: $($markdownFile.FullName)")
    }

    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines($markdownFile.FullName))
    {
        $lineNumber++
        if ($line -match '[ \t]+$')
        {
            $errors.Add("Trailing whitespace: $($markdownFile.FullName):$lineNumber")
        }
    }

    foreach ($match in $linkPattern.Matches($content))
    {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if ($target.StartsWith('#', [StringComparison]::Ordinal) -or
            $target.StartsWith('http://', [StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith('mailto:', [StringComparison]::OrdinalIgnoreCase))
        {
            continue
        }

        $pathPart = $target.Split('#', 2)[0].Split('?', 2)[0]
        if ([string]::IsNullOrWhiteSpace($pathPart))
        {
            continue
        }

        $decodedPath = [Uri]::UnescapeDataString($pathPart)
        $resolvedPath = Join-Path $markdownFile.DirectoryName $decodedPath
        if (-not (Test-Path -LiteralPath $resolvedPath))
        {
            $relativeMarkdownPath = [System.IO.Path]::GetRelativePath($repositoryRoot, $markdownFile.FullName)
            $errors.Add("Broken local link in ${relativeMarkdownPath}: $target")
        }
    }
}

if ($errors.Count -gt 0)
{
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Couplet repository and roadmap baseline verified ($($requiredFiles.Count) required files)."
