<#
.SYNOPSIS
Builds the Windows installer from a verified self-contained publish folder.
.EXAMPLE
./scripts/build_installer.ps1 -AppDirectory artifacts/app -OutputDirectory artifacts/release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $AppDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $Compiler
)

$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $app 'snowpatcher-update.json') -Raw | ConvertFrom-Json
if ($manifest.formatVersion -ne 1 -or $manifest.platform -ne 'win-x64' -or
    $manifest.version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'A Windows release with update metadata is required.'
}

$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($app, $file.path))
    if (-not $path.StartsWith($app.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $expected.Add($path)) {
        throw 'The release contains an invalid or duplicate path.'
    }
    $item = Get-Item -LiteralPath $path
    if ($item.LinkType -or $item.Length -ne $file.size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) {
        throw "Release verification failed: $($file.path)"
    }
}
$null = $expected.Add((Join-Path $app 'snowpatcher-update.json'))
foreach ($item in Get-ChildItem -LiteralPath $app -Recurse -Force) {
    if ($item.LinkType -or (-not $item.PSIsContainer -and -not $expected.Contains($item.FullName))) {
        throw "The release contains an unlisted file or link: $($item.FullName)"
    }
}
foreach ($required in @('SnowPatcher.exe', 'SnowPatcher.dll', 'SnowPatcher.Core.dll', 'SnowPatcher.runtimeconfig.json', 'SnowPatcher.deps.json')) {
    if (-not $expected.Contains((Join-Path $app $required))) {
        throw "A required release file is missing: $required"
    }
}

if (-not $Compiler) {
    $Compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    foreach ($candidate in @("${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe", "$env:ProgramFiles/Inno Setup 7/ISCC.exe")) {
        if (-not $Compiler -and (Test-Path -LiteralPath $candidate)) { $Compiler = $candidate }
    }
}
if (-not $Compiler) { throw 'Install Inno Setup 6 or 7, or pass its ISCC.exe path with -Compiler.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.Equals($app, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($app.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installer output must be outside the published application folder.'
}
$null = New-Item -ItemType Directory -Force -Path $output
& $Compiler "/DAppVersion=$($manifest.version)" "/DAppDirectory=$app" "/DReleaseDirectory=$output" (Join-Path $PSScriptRoot '../installer/SnowPatcher.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed.' }
Get-Item -LiteralPath (Join-Path $output "SnowPatcher-$($manifest.version)-win-x64-setup.exe")
