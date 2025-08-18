param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$VersionString
)

if (-not $VersionString) {
    Write-Error "첫 번째 인수로 버전 문자열이 필요합니다. 예: 3.0.0"
    exit 1
}

$root = Split-Path -Parent $PSCommandPath
$csprojPath = Join-Path $root "..\InvenAdClicker.csproj"
if (-not (Test-Path $csprojPath)) {
    Write-Error "csproj를 찾을 수 없습니다: $csprojPath"
    exit 1
}

# csproj의 <Version>만 간단 갱신 (없으면 첫 PropertyGroup에 추가)
$content = Get-Content -LiteralPath $csprojPath -Raw
if ($content -match '<Version>.*?</Version>') {
    $content = [regex]::Replace($content, '<Version>.*?</Version>', "<Version>$VersionString</Version>")
} else {
    $content = [regex]::Replace(
        $content,
        '(?s)(</PropertyGroup>)',
        "  <Version>$VersionString</Version>`r`n$1",
        1
    )
}
Set-Content -LiteralPath $csprojPath -Value $content -Encoding UTF8

# 커밋
git add "$csprojPath"
git commit -m "build: v$VersionString" | Out-Null

# 현재 브랜치
$currentBranch = git rev-parse --abbrev-ref HEAD

# 태그(릴리즈 워크플로우 트리거를 위해 v 접두어 사용)
$tagName = "v$VersionString"
git tag -a -m "$tagName" $tagName

# 브랜치/태그 동시 푸시
git push origin $currentBranch $tagName

Write-Host "완료: $csprojPath의 Version 갱신, 커밋 및 태그($tagName) 푸시됨."

