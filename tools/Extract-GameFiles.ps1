<#
.SYNOPSIS
Extract a SnowRunner PAK, cache block or SSL bundle.
.EXAMPLE
.\Extract-GameFiles.ps1 'D:\Steam\steamapps\common\SnowRunner\preload\paks\client\initial.pak'
.EXAMPLE
.\Extract-GameFiles.ps1 '.\initial.cache_block' -OutputPath 'D:\ModWork\cache'
.EXAMPLE
.\Extract-GameFiles.ps1 '.\downloads'
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position=0, ValueFromRemainingArguments=$true)][string[]]$InputPath,
    [string]$OutputPath,
    [switch]$Help
)
. "$PSScriptRoot\_Common.ps1"
if ($Help) { Get-Help $PSCommandPath -Detailed; return }
try {
    $paths = @(Get-SnowPaths $InputPath)
    $files = @(foreach ($path in $paths) {
        if ([IO.Directory]::Exists($path)) {
            Get-SnowFolderFiles $path | Where-Object { $_.Extension -in @('.pak', '.zip', '.cache_block', '.sslbundle') } | ForEach-Object { $_.FullName }
        } else { $path }
    })
    if ($files.Count -eq 0) { throw 'No supported containers found.' }
    if ($OutputPath -and $files.Count -ne 1) { throw '-OutputPath requires exactly one input file.' }
    foreach ($file in $files) {
        Write-Host "Extracting $file"
        $destination = Expand-SnowContainer $file $OutputPath
        Write-Host 'Extracted to:'
        Write-Output $destination
    }
} catch { Write-Error $_.Exception.Message -ErrorAction Continue; exit 1 }
