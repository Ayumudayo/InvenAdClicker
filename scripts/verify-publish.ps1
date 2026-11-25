param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [switch]$InstallChromium
)

$ErrorActionPreference = 'Stop'
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$required = @(
    'InvenAdClicker.exe',
    'Microsoft.Playwright.dll',
    'playwright.ps1',
    'appsettings.json',
    '.playwright/node/win32_x64/node.exe',
    '.playwright/package/cli.js',
    '.playwright/package/browsers.json'
)
foreach ($relativePath in $required) {
    $asset = Join-Path $publishPath $relativePath
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf) -or (Get-Item -LiteralPath $asset).Length -eq 0) {
        throw "Missing or empty publish asset: $asset"
    }
}

$playwrightScript = Join-Path $publishPath 'playwright.ps1'
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $playwrightScript --version
if ($LASTEXITCODE -ne 0) { throw "Published Playwright CLI failed: $LASTEXITCODE" }
if ($InstallChromium) {
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $playwrightScript install chromium
    if ($LASTEXITCODE -ne 0) { throw "Chromium installation failed: $LASTEXITCODE" }
}

& (Join-Path $publishPath 'InvenAdClicker.exe') --smoke-test
if ($LASTEXITCODE -ne 0) { throw "Published executable smoke test failed: $LASTEXITCODE" }
Write-Host 'Publish assets, Playwright driver, DI, and headless Chromium smoke test passed.'
