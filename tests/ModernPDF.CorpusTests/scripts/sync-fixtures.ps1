param(
    [ValidateSet("smoke", "full")]
    [string]$Profile = "smoke",
    [switch]$IncludeDisabled
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$fixturesRoot = Join-Path $scriptRoot "..\\fixtures"
$manifestPath = Join-Path $fixturesRoot "manifest.json"
$downloadRoot = Join-Path $fixturesRoot "downloaded"

if (-not (Test-Path $manifestPath)) {
    throw "Manifest file not found at '$manifestPath'."
}

$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json -Depth 12
$fixtures = @($manifest.fixtures | Where-Object { $_.profiles -contains $Profile })

if (-not $IncludeDisabled) {
    $fixtures = @($fixtures | Where-Object { $_.enabled -eq $true })
}

if ($fixtures.Count -eq 0) {
    Write-Host "No fixtures selected for profile '$Profile'." -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Path $downloadRoot)) {
    New-Item -Path $downloadRoot -ItemType Directory -Force | Out-Null
}

$syncedCount = 0
foreach ($fixture in $fixtures) {
    $relativeLocalPath = $fixture.localPath -replace "/", [System.IO.Path]::DirectorySeparatorChar
    $targetPath = Join-Path $downloadRoot $relativeLocalPath
    $targetDirectory = Split-Path -Parent $targetPath
    if (-not (Test-Path $targetDirectory)) {
        New-Item -Path $targetDirectory -ItemType Directory -Force | Out-Null
    }

    $sourceUrl = "https://raw.githubusercontent.com/$($fixture.sourceRepository)/$($fixture.sourceCommit)/$($fixture.sourcePath)"
    Invoke-WebRequest -Uri $sourceUrl -OutFile $targetPath

    $actualHash = (Get-FileHash -Path $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = "$($fixture.sha256)".ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Checksum mismatch for '$($fixture.id)'. Expected '$expectedHash' but got '$actualHash'."
    }

    $syncedCount++
    Write-Host "Synced $($fixture.id) -> $targetPath" -ForegroundColor Green
}

Write-Host "Done. Synced $syncedCount fixture(s) for profile '$Profile'." -ForegroundColor Cyan
