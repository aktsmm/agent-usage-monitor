[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\local\distribution')
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
$props = [xml](Get-Content -LiteralPath '.\Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.VersionPrefix
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VersionPrefix must be a stable x.y.z version.' }
if (!(Test-Path -LiteralPath '.\LICENSE')) {
    throw 'A finalized project LICENSE is required before packaging.'
}
$output = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) { throw "Output directory is not empty: $output. Refusing overwrite." }
}
$publish = Join-Path $output 'publish'
$release = Join-Path $output 'release'
New-Item -ItemType Directory -Path $publish, $release -Force | Out-Null
function Invoke-Checked { param([scriptblock]$Command) & $Command; if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE)." } }
Invoke-Checked { dotnet tool restore --verbosity quiet }
Invoke-Checked { dotnet restore .\AiUsageTray.slnx --locked-mode --verbosity quiet }
Invoke-Checked { dotnet build .\AiUsageTray.slnx -c Release --no-restore --nologo --verbosity quiet -m:1 -nr:false -p:UseSharedCompilation=false }
Invoke-Checked { dotnet run --project .\tests\AiUsageTray.Tests -c Release --no-build }
Invoke-Checked {
    dotnet publish .\src\AiUsageTray.Desktop -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        -p:IncludeSourceRevisionInInformationalVersion=false `
        -p:UseSharedCompilation=false -nr:false `
        --output $publish --nologo --verbosity quiet
}
if (!(Test-Path -LiteralPath (Join-Path $publish 'ja\AiUsageTray.Core.resources.dll'))) {
    throw 'Japanese satellite resources are missing from the self-contained publish.'
}
$validation = Start-Process -FilePath (Join-Path $publish 'AiUsageTray.Desktop.exe') `
    -ArgumentList '--validate-resources' -Wait -PassThru
if ($validation.ExitCode -ne 0) { throw 'Published localization resources could not be loaded.' }
$assets = Get-Content -LiteralPath '.\src\AiUsageTray.Desktop\obj\project.assets.json' -Raw | ConvertFrom-Json -AsHashtable
$packageFolders = @($assets.packageFolders.Keys)
$notices = Join-Path $publish 'third-party-licenses'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
foreach ($runtime in @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $key = @($assets.libraries.Keys | Where-Object { $_.StartsWith("$runtime/", [StringComparison]::OrdinalIgnoreCase) })
    if ($key.Count -eq 1) {
        $relative = $assets.libraries[$key[0]].path
    } else {
        $dependency = @($assets.project.frameworks.Values | ForEach-Object { $_.downloadDependencies } |
            Where-Object { $_.name -eq $runtime })[0]
        if (!$dependency -or $dependency.version -notmatch '^\[([0-9.]+), \1\]$') {
            throw "Could not resolve exact runtime license package: $runtime"
        }
        $relative = $runtime.ToLowerInvariant() + '/' + $Matches[1]
    }
    $package = @($packageFolders | ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ })[0]
    if (!$package) { throw "Runtime package folder missing: $runtime" }
    $licenseFiles = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match 'LICENSE|NOTICE' })
    if ($licenseFiles.Count -eq 0) { throw "Runtime original notices missing: $runtime" }
    foreach ($file in $licenseFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $notices ($runtime + '-' + $file.Name))
    }
}
foreach ($file in @('README.md', 'PRIVACY.md', 'SECURITY.md', 'THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $publish
}
Copy-Item -LiteralPath '.\docs' -Destination $publish -Recurse
if (Test-Path -LiteralPath '.\LICENSE') {
    Copy-Item -LiteralPath '.\LICENSE' -Destination $publish
    Copy-Item -LiteralPath '.\docs\licenses\CC-BY-NC-SA-4.0.txt' -Destination $publish
}
# A unique packId does not share the usage-data directory, which must survive uninstall.
Invoke-Checked {
    dotnet tool run vpk pack -u AiUsageTrayDesktop -v $version -p $publish -o $release `
        --mainExe AiUsageTray.Desktop.exe --packTitle 'Agent Usage Monitor' --packAuthors aktsmm `
        --runtime win-x64 --shortcuts StartMenuRoot --delta None --icon .\src\AiUsageTray.Desktop\assets\app.ico
}
$expected = @('AiUsageTrayDesktop-win-Setup.exe', 'AiUsageTrayDesktop-win-Portable.zip',
    "AiUsageTrayDesktop-$version-full.nupkg", 'releases.win.json')
foreach ($name in $expected) {
    if (!(Test-Path -LiteralPath (Join-Path $release $name))) { throw "Expected asset missing: $name" }
}
$sums = Get-ChildItem -LiteralPath $release -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem -LiteralPath $release -File | Sort-Object Name |
    Select-Object Name, Length, @{Name='SHA256';Expression={(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}}
