param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'coverage.cobertura.xml' -Recurse -File)
if ($reports.Count -eq 0) { throw "No Cobertura coverage reports found in $ResultsDirectory" }
[long]$linesCovered = 0
[long]$linesValid = 0
[long]$branchesCovered = 0
[long]$branchesValid = 0
foreach ($report in $reports) {
    [xml]$coverage = Get-Content -LiteralPath $report.FullName -Raw
    $linesCovered += [long]$coverage.coverage.'lines-covered'
    $linesValid += [long]$coverage.coverage.'lines-valid'
    $branchesCovered += [long]$coverage.coverage.'branches-covered'
    $branchesValid += [long]$coverage.coverage.'branches-valid'
}
if ($linesValid -eq 0) { throw 'Coverage contains no instrumented lines. Build portable PDBs before running coverage.' }
$lineRate = '{0:P2}' -f ($linesCovered / $linesValid)
$branchRate = if ($branchesValid -gt 0) { '{0:P2}' -f ($branchesCovered / $branchesValid) } else { 'N/A' }
$summary = @"
## Test coverage

| Metric | Covered / total | Rate |
|---|---:|---:|
| Lines | $linesCovered / $linesValid | $lineRate |
| Branches | $branchesCovered / $branchesValid | $branchRate |

Cobertura and TRX files are available in the test-results artifact.
"@
Write-Output $summary
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8
}
