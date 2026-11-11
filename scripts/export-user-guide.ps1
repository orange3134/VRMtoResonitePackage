# Create a Notion import ZIP from the tracked user-guide sources.
# Run from any directory with Windows PowerShell 5.1 or later.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$guideRoot = Join-Path $repoRoot 'docs/user-guide'
$pages = @('ja.md', 'en.md')
$files = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($page in $pages) { [void]$files.Add($page) }
$utf8 = [Text.UTF8Encoding]::new($false, $true)

# Validate every Markdown link before writing the archive. Only the two pages
# and their referenced assets belong in the public import, not the maintainer README.
foreach ($page in $pages) {
    $markdown = [IO.File]::ReadAllText((Join-Path $guideRoot $page), $utf8)
    foreach ($match in [regex]::Matches($markdown, '\]\(([^)]+)\)')) {
        $target = [Uri]::UnescapeDataString($match.Groups[1].Value)
        if ($target -match '^https?://') { continue }
        if ($target -notin $pages -and $target -notmatch '^assets/[A-Za-z0-9_-]+\.png$') {
            throw "Unsupported local link in ${page}: $target"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $guideRoot $target) -PathType Leaf)) {
            throw "Missing local link in ${page}: $target"
        }
        [void]$files.Add($target)
    }
}

$outputDirectory = Join-Path $repoRoot '.tmp_verify'
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$outputPath = Join-Path $outputDirectory 'user-guide-notion.zip'
$stream = [IO.File]::Open($outputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in ($files | Sort-Object)) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                (Join-Path $guideRoot $file),
                ('user-guide/' + $file),
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

Write-Output "Validated $($pages.Count) pages and packaged $($files.Count) files: $outputPath"
