# Mutation-tests the rating self-check: breaks one rule at a time and requires the check to catch it.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$math = Join-Path $root 'src\Rating\EloMath.cs'
$perf = Join-Path $root 'src\Rating\Performance.cs'
$proj = Join-Path $PSScriptRoot 'elocheck.csproj'

$mutations = @(
    @{ Name = 'inverted expectation exponent'; File = $math
       From = '(opponent - own) / 400.0'; To = '(own - opponent) / 400.0' }

    @{ Name = 'shrinkage weights swapped'; File = $math
       From = '(rounds * meanActual + confidence * meanExpected) / (rounds + confidence)'
       To = '(confidence * meanActual + rounds * meanExpected) / (rounds + confidence)' }

    @{ Name = 'envelope clamp removed'; File = $math
       From = 'outcome = Math.Clamp(outcome, -i.K * meanExpected, i.K * (1.0 - meanExpected));'
       To = '' }

    @{ Name = 'sign floor removed'; File = $math
       From = 'return SignFloor(outcome, outcome + i.PerformanceBonus * (i.Percentile - 0.5));'
       To = 'return outcome + i.PerformanceBonus * (i.Percentile - 0.5);' }

    @{ Name = 'penalty not deducted'; File = $math
       From = 'Math.Max(minRating, rating + matchDelta - penalty)'
       To = 'Math.Max(minRating, rating + matchDelta)' }

    @{ Name = 'ties not sharing a rank'; File = $perf
       From = '(below + 0.5 * (tied - 1)) / (all.Length - 1)'
       To = '(below + 0.5 * tied) / (all.Length - 1)' }

    @{ Name = 'published 13-0 figure drifts'; File = $math
       From = 'var participation = Math.Min(1.0, (double)i.RoundsPresent / i.MatchRounds);'
       To = 'var participation = Math.Min(1.0, (double)i.RoundsPresent / i.MatchRounds) * 1.05;' }
)

$originals = @{ $math = Get-Content $math -Raw; $perf = Get-Content $perf -Raw }
$caught = 0

try {
    foreach ($m in $mutations) {
        $text = $originals[$m.File]
        if (-not $text.Contains($m.From)) { throw "mutation '$($m.Name)' did not match its anchor" }

        Set-Content $m.File ($text.Replace($m.From, $m.To)) -NoNewline
        $out = & dotnet run --project $proj --nologo -v q 2>&1 | Out-String
        Set-Content $m.File $originals[$m.File] -NoNewline

        if ($out -match 'FAIL') {
            $caught++
            $reason = ($out -split "`n" | Where-Object { $_ -match 'FAIL' } | Select-Object -First 1).Trim()
            '  CAUGHT   {0,-32} {1}' -f $m.Name, ($reason -replace '^FAIL - Ranks_EloPoints: rating self-check failed . ', '')
        }
        else {
            '  MISSED   {0,-32} self-check still passed' -f $m.Name
        }
    }
}
finally {
    foreach ($f in $originals.Keys) { Set-Content $f $originals[$f] -NoNewline }
}

''
'{0} of {1} mutations caught' -f $caught, $mutations.Count
if ($caught -ne $mutations.Count) { exit 1 }
