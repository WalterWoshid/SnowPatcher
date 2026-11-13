<#
.SYNOPSIS
Repack a folder created by Extract-GameFiles into a new file.
.EXAMPLE
.\Pack-GameFiles.ps1 'D:\ModWork\cache' -OutputPath 'D:\ModWork\initial.cache_block'
.EXAMPLE
.\Pack-GameFiles.ps1 'D:\ModWork\initial.pak-extracted'
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
    if ($OutputPath -and $paths.Count -ne 1) { throw '-OutputPath requires exactly one input folder.' }
    foreach ($path in $paths) {
        if (-not [IO.Directory]::Exists($path)) { throw "Supply a folder created by Extract-GameFiles.ps1: $path" }
        Write-Host "Repacking $path"
        $destination = Compress-SnowContainer $path $OutputPath
        Write-Host 'Verified output:'
        Write-Output $destination
    }
} catch { Write-Error $_.Exception.Message -ErrorAction Continue; exit 1 }
