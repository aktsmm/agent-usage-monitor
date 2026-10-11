[CmdletBinding()]
param([string]$Destination = (Join-Path $PSScriptRoot '..\local\public-snapshot'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) {
    if (@(Get-ChildItem -LiteralPath $destinationPath -Force).Count -gt 0) { throw 'Snapshot destination must be empty.' }
}
$rootFiles = @('.gitignore', '.gitattributes', 'AiUsageTray.slnx', 'Directory.Build.props', 'global.json',
    'README.md', 'LICENSE', 'PRIVACY.md', 'SECURITY.md', 'THIRD-PARTY-NOTICES.txt')
$files = @($rootFiles | ForEach-Object { Get-Item -LiteralPath (Join-Path $root $_) })
foreach ($folder in @('src', 'tests', 'scripts', 'docs', '.config', '.github')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $root $folder) -Recurse -File -Force |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|local|artifacts|\.git)[\\/]' }
}
$allowedExtensions = @('.cs', '.csproj', '.xaml', '.props', '.slnx', '.json', '.ps1', '.md', '.txt', '.html', '.yml', '.png')
$manifest = @()
foreach ($file in $files | Sort-Object FullName -Unique) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked source: $($file.Name)" }
    if ($file.Name -notin @('LICENSE', '.gitignore', '.gitattributes') -and $file.Extension -notin $allowedExtensions) {
        throw "Unapproved source type: $($file.Name)"
    }
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
    if ($relative.StartsWith('..') -or $relative -match '(?i)(auth\.json|token|credential|internal-.*\.md|session-state)') {
        # Code named WindowsCredential.cs is authored source, not credential material.
        if ($relative -ne 'src\AiUsageTray.Diagnostics\WindowsCredential.cs') { throw "Unexpected sensitive path: $relative" }
    }
    $target = Join-Path $destinationPath $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    $manifest += [ordered]@{
        Path = $relative.Replace('\','/')
        Bytes = $file.Length
        SHA256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destinationPath 'SNAPSHOT-FILES.json') -Encoding utf8
if (Test-Path -LiteralPath (Join-Path $destinationPath '.git')) { throw 'Git history must not be exported.' }
[pscustomobject]@{ Destination=$destinationPath; Files=$manifest.Count; IncludesHistory=$false }
