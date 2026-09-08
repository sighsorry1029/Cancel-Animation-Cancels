param (
    [Parameter(Mandatory = $true)]
    [string] $manifestFile,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $versionString
)

$ErrorActionPreference = 'Stop'
$manifestPath = (Resolve-Path -LiteralPath $manifestFile).ProviderPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$null = $manifest | ConvertFrom-Json
$versionPattern = '("version_number"\s*:\s*")[^"]*(")'
if ([regex]::Matches($manifest, $versionPattern).Count -ne 1) {
    throw 'The manifest must contain exactly one version_number string.'
}

$manifest = [regex]::Replace($manifest, $versionPattern, '${1}' + $versionString + '${2}')
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))
