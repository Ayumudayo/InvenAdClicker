param(
    [string]$OldBuild = 'bin/Release/Copy',
    [string]$NewBuild = 'artifacts/build-comparison/publish',
    [ValidateRange(1,10000)][int]$Urls = 5,
    [ValidateRange(1,100)][int]$Pairs = 5,
    [switch]$OldUsesDataDirectory,
    [string]$Output = 'artifacts/build-comparison/five-urls'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location -LiteralPath $repo
$oldSource = (Resolve-Path $OldBuild).Path
$newSource = (Resolve-Path $NewBuild).Path
$outputPath = [IO.Path]::GetFullPath((Join-Path $repo $Output))
if (-not $outputPath.StartsWith((Join-Path $repo 'artifacts/'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be inside repository artifacts.' }
if (Test-Path -LiteralPath $outputPath) { throw 'Use a fresh output directory.' }
if (Get-Process InvenAdClicker -ErrorAction SilentlyContinue) { throw 'Close existing InvenAdClicker processes first.' }
$sourceFiles = @(Get-ChildItem -LiteralPath $oldSource -File -Recurse | ForEach-Object { [pscustomobject]@{ Path=$_.FullName; Hash=(Get-FileHash -LiteralPath $_.FullName).Hash } })
$settings = Get-Content -LiteralPath (Join-Path $oldSource 'appsettings.json') -Raw | ConvertFrom-Json
$settings.AppSettings.DryRun = $true
$settings.AppSettings.TargetUrls = @($settings.AppSettings.TargetUrls | Select-Object -First $Urls | Select-Object -Unique)
if ($settings.AppSettings.Debug.Enabled) { throw 'Comparison requires normal headless settings.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$builds = @{}
foreach ($name in @('old','new')) {
    $destination = Join-Path $outputPath "$name-build"
    New-Item -ItemType Directory -Path $destination | Out-Null
    $source = if ($name -eq 'old') { $oldSource } else { $newSource }
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse
    # Both programs receive identical copied settings and encrypted credentials.
    $settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $destination 'appsettings.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $oldSource 'credentials.dat') -Destination $destination -Force
    $builds[$name] = $destination
}
$controller = Join-Path $PSScriptRoot 'bin/Release/net8.0/InvenAdClicker.BuildComparison.dll'
$results = @()
try {
    # One warm-up per build is excluded, followed by alternating paired order.
    for ($pair = 0; $pair -le $Pairs; $pair++) {
        $order = if ($pair % 2 -eq 0) { @('old','new') } else { @('new','old') }
        foreach ($name in $order) {
            $run = Join-Path $outputPath "$pair-$name"
            New-Item -ItemType Directory -Path $run | Out-Null
            $settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'appsettings.json') -Encoding utf8
            Copy-Item -LiteralPath (Join-Path $oldSource 'credentials.dat') -Destination $run
            $report = Join-Path $run 'result.json'
            if ($name -eq 'new' -or $OldUsesDataDirectory) {
                & dotnet $controller (Join-Path $builds[$name] 'InvenAdClicker.exe') $run $report --explicit-data-directory
            } else {
                & dotnet $controller (Join-Path $builds[$name] 'InvenAdClicker.exe') $run $report
            }
            if ($LASTEXITCODE -ne 0) { throw "Comparison run failed: $pair-$name. Inspect isolated output." }
            $results += [pscustomobject]@{ Pair=$pair; Build=$name; Warmup=($pair -eq 0); Report=$report }
        }
    }
}
finally {
    $changed = @($sourceFiles | Where-Object { (Get-FileHash -LiteralPath $_.Path).Hash -ne $_.Hash })
    if ($changed.Count) { throw 'Original old build changed during comparison.' }
    [pscustomobject]@{
        OriginalOldBuildUnchanged = $true
        OriginalFilesVerified = $sourceFiles.Count
        OldExecutableSha256 = (Get-FileHash -LiteralPath (Join-Path $oldSource 'InvenAdClicker.exe')).Hash
        NewExecutableSha256 = (Get-FileHash -LiteralPath (Join-Path $newSource 'InvenAdClicker.exe')).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'integrity.json') -Encoding utf8
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'index.json') -Encoding utf8
}
