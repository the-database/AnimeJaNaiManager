#requires -Version 7
param([string]$CanonicalSourcePath = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin = Get-Content (Join-Path $root 'addon-sdk-source.json') -Raw | ConvertFrom-Json
$copy = Join-Path $root 'AnimeJaNaiConfEditor/Services/AddonHostClient.cs'
# GitHub serves LF; normalize checkout line endings before comparing content.
function ContentHash([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text.Replace("`r`n", "`n")))).ToLowerInvariant()
}
if ((ContentHash ([IO.File]::ReadAllText($copy))) -ne $pin.sha256) { throw 'Manager management client differs from its pinned SDK checksum.' }
if ($CanonicalSourcePath) {
    $hash = ContentHash ([IO.File]::ReadAllText($CanonicalSourcePath))
    if ($hash -ne $pin.sha256) { throw 'Canonical management client differs from the Manager copy.' }
}
Write-Host 'PASS pinned management client snapshot.'
