param([Parameter(Mandatory=$true)][string[]]$Directories, [string]$Output = 'artifacts/build-comparison/summary.json')
$ErrorActionPreference = 'Stop'
function Median($Values) {
    $sorted = @($Values | Sort-Object)
    if (-not $sorted.Count) { throw 'No measured runs.' }
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return [double]$sorted[$middle] }
    return ([double]$sorted[$middle-1] + [double]$sorted[$middle]) / 2
}
$groups = foreach ($directory in $Directories) {
    $index = Get-Content -LiteralPath (Join-Path $directory 'index.json') -Raw | ConvertFrom-Json
    $runs = @($index | Where-Object { -not $_.Warmup } | ForEach-Object {
        $result = Get-Content -LiteralPath $_.Report -Raw | ConvertFrom-Json
        [pscustomobject]@{ Pair=$_.Pair; Build=$_.Build; Result=$result }
    })
    $builds = foreach ($build in @('old','new')) {
        $results = @($runs | Where-Object Build -eq $build | ForEach-Object Result)
        [pscustomobject]@{
            Build = $build; Runs = $results.Count
            WallSecondsMedian = (Median @($results | ForEach-Object { $_.WallMilliseconds/1000 }))
            WallSecondsMin = ($results.WallMilliseconds | Measure-Object -Minimum).Minimum/1000
            WallSecondsMax = ($results.WallMilliseconds | Measure-Object -Maximum).Maximum/1000
            CpuSecondsMedian = (Median @($results | ForEach-Object { $_.Resources.CpuSeconds }))
            PeakPrivateMiBMedian = (Median @($results | ForEach-Object { $_.Resources.PeakPrivateBytes/1MB }))
            PeakWorkingSetMiBMedian = (Median @($results | ForEach-Object { $_.Resources.PeakSummedWorkingSetBytes/1MB }))
            LinksPerRun = @($results.CollectedLinks)
            EmptyUrlsPerRun = @($results.EmptyUrls)
            ExitCodes = @($results.ExitCode)
            UnreadableProcesses = @($results | ForEach-Object { $_.Resources.UnreadableProcesses })
            RemainingProcesses = @($results | ForEach-Object { $_.Resources.RemainingProcesses })
        }
    }
    $perUrl = foreach ($url in $runs[0].Result.Settings.TargetUrls) {
        $counts = foreach ($build in @('old','new')) {
            $collections = @($runs | Where-Object Build -eq $build | ForEach-Object { $_.Result.Collections | Where-Object Url -eq $url })
            [pscustomobject]@{ Build=$build; Links=@($collections.Links); MedianCollectionMilliseconds=(Median @($collections.Milliseconds)) }
        }
        [pscustomobject]@{ Url=$url; Builds=@($counts) }
    }
    [pscustomobject]@{
        Directory=$directory; TargetUrls=$runs[0].Result.Settings.TargetUrls.Count
        Builds=@($builds)
        ReductionPercent=[pscustomobject]@{
            Wall=100*(1-$builds[1].WallSecondsMedian/$builds[0].WallSecondsMedian)
            Cpu=100*(1-$builds[1].CpuSecondsMedian/$builds[0].CpuSecondsMedian)
            PeakPrivate=100*(1-$builds[1].PeakPrivateMiBMedian/$builds[0].PeakPrivateMiBMedian)
        }
        PairedWallReductionsPercent=@(foreach ($pair in ($runs.Pair | Select-Object -Unique)) {
            $paired = @($runs | Where-Object Pair -eq $pair)
            $old = ($paired | Where-Object Build -eq 'old').Result.WallMilliseconds
            $new = ($paired | Where-Object Build -eq 'new').Result.WallMilliseconds
            100*(1-$new/$old)
        })
        PerUrl=@($perUrl)
    }
}
ConvertTo-Json -InputObject @($groups) -Depth 12 | Set-Content -LiteralPath $Output -Encoding utf8
$groups | Select-Object TargetUrls,Builds,ReductionPercent
