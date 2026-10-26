function Get-Expected($own, $opp) {
    1.0 / (1.0 + [Math]::Pow(10.0, ($opp - $own) / 400.0))
}

function Get-Delta($K, $c, $n, $Nm, $Sbar, $Ebar) {
    $adj = ($n * $Sbar + $c * $Ebar) / ($n + $c)
    $base = $K * ($adj - $Ebar) * ($n / $Nm)
    $lo = -$K * $Ebar
    $hi = $K * (1.0 - $Ebar)
    [Math]::Round([Math]::Max($lo, [Math]::Min($hi, $base)), 2)
}

'Expected even           = {0:N4}' -f (Get-Expected 1000 1000)
'Expected +400 favourite = {0:N4}' -f (Get-Expected 1400 1000)
'Expected pair sums to 1 = {0:N4}' -f ((Get-Expected 1400 1000) + (Get-Expected 1000 1400))
''
'13-0 even  unshrunk     = {0}' -f (Get-Delta 50 0 13 13 1.0 0.5)
'13-0 even  c=10         = {0}' -f (Get-Delta 50 10 13 13 1.0 0.5)
'13-8 full  unshrunk     = {0}' -f (Get-Delta 50 0 21 21 (13 / 21) 0.5)
'13-8 full  c=10         = {0}' -f (Get-Delta 50 10 21 21 (13 / 21) 0.5)
'join r18 4-0 unshrunk   = {0}' -f (Get-Delta 50 0 4 21 1.0 0.5)
'join r18 4-0 c=10       = {0}' -f (Get-Delta 50 10 4 21 1.0 0.5)
'2-round cameo c=10      = {0}' -f (Get-Delta 50 10 2 21 1.0 0.5)
''
'per-round rate ratio unshrunk = {0:N2}x' -f (((Get-Delta 50 0 4 21 1.0 0.5) / 4) / ((Get-Delta 50 0 21 21 (13 / 21) 0.5) / 21))
'per-round rate ratio c=10     = {0:N2}x' -f (((Get-Delta 50 10 4 21 1.0 0.5) / 4) / ((Get-Delta 50 10 21 21 (13 / 21) 0.5) / 21))
''
'--- abandon, heavy favourite E=0.9, match runs 24 rounds ---'
$penalty = 25
$stay = Get-Delta 50 10 24 24 0.0 0.9
'stay to end, 0-24       = {0}' -f $stay
''

# Leaving earlier accrues less loss, so the penalty has to exceed what staying
# would still have cost. That is what makes early abandonment the hard case.
foreach ($leaveAt in 4, 8, 14, 20) {
    $won = [Math]::Floor($leaveAt / 4)
    $part = Get-Delta 50 10 $leaveAt 24 ($won / $leaveAt) 0.9
    $onTop = [Math]::Round($part - $penalty, 2)
    $verdict = if ($onTop -lt $stay) { 'deterred' } else { 'NOT deterred' }
    'leave at r{0,-2} {1}-{2,-2} accrued {3,7}  on top {4,7}  vs stay {5,7}  -> {6}' -f `
        $leaveAt, $won, ($leaveAt - $won), $part, $onTop, $stay, $verdict
}
''
'penalty that always deters = K * E = {0}' -f (50 * 0.9)
'REPLACE at r14: leaving {0} vs staying {1} -> {2}' -f -$penalty, $stay, $(if (-$penalty -lt $stay) { 'deterred' } else { 'NOT deterred - exploit' })
