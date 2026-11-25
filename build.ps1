# 사용법: powershell -ExecutionPolicy Bypass -File build.ps1 [-Package]
param([switch]$Package)

$ErrorActionPreference = "Stop"
Push-Location -LiteralPath $PSScriptRoot
try {
    Write-Host "InvenAdClicker 릴리스 빌드"
    cargo build --release --locked
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $previousEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $metadata = cargo metadata --no-deps --format-version 1 --locked
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } finally { [Console]::OutputEncoding = $previousEncoding }
    $target = ($metadata | ConvertFrom-Json).target_directory
    $exe = Join-Path $target "release\InvenAdClicker.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "실행 파일을 찾지 못했습니다. $exe" }
    $config = Join-Path (Split-Path -Parent $exe) "config.json"
    $template = Join-Path $PSScriptRoot "config.json.example"
    if (-not [IO.File]::Exists($config)) {
        try { [IO.File]::Copy($template, $config, $false) }
        catch [IO.IOException] { if (-not [IO.File]::Exists($config)) { throw } }
    }
    Write-Host "빌드 완료: $exe"
    Write-Host "설정 파일: $config"

    if ($Package) {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $artifactsDir = Join-Path $PSScriptRoot "artifacts"
        New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
        $destination = Join-Path $artifactsDir "InvenAdClicker.zip"
        $temporary = Join-Path $artifactsDir ([IO.Path]::GetRandomFileName())
        try {
            $zip = [IO.Compression.ZipFile]::Open($temporary, 'Create')
            try {
                $files = @('Cargo.toml', 'Cargo.lock', 'build.ps1', 'build.sh', 'config.json.example', '.gitignore', 'README.md', 'LICENSE')
                $files += Get-ChildItem -LiteralPath 'src' -Filter '*.rs' -File -Recurse | ForEach-Object {
                    [IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName).Replace('\', '/')
                }
                foreach ($file in $files) {
                    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file) -PathType Leaf) {
                        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot $file), "InvenAdClicker/$file")
                    }
                }
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $template, "InvenAdClicker/config.json")
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $exe, "InvenAdClicker/InvenAdClicker.exe")
            } finally { $zip.Dispose() }
            if ([IO.File]::Exists($destination)) {
                [IO.File]::Replace($temporary, $destination, [System.Management.Automation.Language.NullString]::Value)
            } else {
                [IO.File]::Move($temporary, $destination)
            }
        } finally {
            if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        }
        Write-Host "배포 ZIP: $destination"
    }
} finally { Pop-Location }
