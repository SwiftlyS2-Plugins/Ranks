# Mutation test for ComboSelfCheck: breaks one rule at a time and expects the check to catch it.
# A mutation that still passes means the check does not actually cover that rule.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$mutations = @(
    @{ Name = 'tie-break by last instead of config order'; File = 'src\Combos\ComboMatcher.cs'
       From = ': a.Order.CompareTo(b.Order));'; To = ': b.Order.CompareTo(a.Order));' },

    @{ Name = 'least specific combo wins'; File = 'src\Combos\ComboMatcher.cs'
       From = '? b.RequiresAll.Count.CompareTo(a.RequiresAll.Count)'; To = '? a.RequiresAll.Count.CompareTo(b.RequiresAll.Count)' },

    @{ Name = 'RequiresNone stops excluding'; File = 'src\Combos\ComboMatcher.cs'
       From = 'if (test.Matches(facts)) return false;'; To = 'if (false) return false;' },

    @{ Name = 'Count pays on the first kill'; File = 'src\Combos\ComboMatcher.cs'
       From = 'if (winner is null && seen >= rule.Count) winner = rule;'; To = 'if (winner is null && seen >= 1) winner = rule;' },

    @{ Name = 'Count+suppression no longer rejected'; File = 'src\Combos\ComboMatcher.cs'
       From = 'if (count > 1 && suppressing)'; To = 'if (count > 99 && suppressing)' },

    @{ Name = 'empty RequiresAll accepted'; File = 'src\Combos\ComboMatcher.cs'
       From = 'if (entry.RequiresAll.Count == 0)'; To = 'if (entry.RequiresAll.Count == 99)' },

    @{ Name = 'losing Count combo stops accumulating'; File = 'src\Combos\ComboMatcher.cs'
       From = 'if (winner is null && seen >= rule.Count) winner = rule;'; To = 'if (seen >= rule.Count) return rule;' },

    @{ Name = 'distance compares the wrong way'; File = 'src\Combos\ComboTypes.cs'
       From = 'AttributeKind.Distance => facts.Distance >= Number,'; To = 'AttributeKind.Distance => facts.Distance <= Number,' },

    @{ Name = 'weapon_ prefix no longer stripped'; File = 'src\Combos\WeaponClasses.cs'
       From = '? weapon["weapon_".Length..]'; To = '? weapon' }
)

$caught = 0

foreach ($m in $mutations) {
    $path = Join-Path $root $m.File
    $original = Get-Content $path -Raw

    if (-not $original.Contains($m.From)) {
        Write-Host ("  SKIP  {0} - anchor not found, update the script" -f $m.Name) -ForegroundColor Yellow
        continue
    }

    ($original -replace [regex]::Escape($m.From), $m.To) | Set-Content $path -NoNewline

    try {
        dotnet run --project (Join-Path $PSScriptRoot 'combocheck.csproj') -v q --nologo *>&1 | Out-Null
        $failed = $LASTEXITCODE -ne 0
    }
    finally {
        $original | Set-Content $path -NoNewline
    }

    if ($failed) {
        $caught++
        Write-Host ("  caught  {0}" -f $m.Name) -ForegroundColor Green
    }
    else {
        Write-Host ("  MISSED  {0}" -f $m.Name) -ForegroundColor Red
    }
}

Write-Host ""
Write-Host ("{0}/{1} mutations caught" -f $caught, $mutations.Count)

exit ($(if ($caught -eq $mutations.Count) { 0 } else { 1 }))
