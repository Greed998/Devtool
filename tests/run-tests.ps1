# DevToolbox – minden teszt futtatása Windowson.   Használat:  .\run-tests.ps1 [unit|e2e|ui|all]
# RENDSZERGAZDAI PowerShellből (a teszt-szerverek http://+:port címen figyelnek).
# Kell: .NET 10 SDK. A böngészős részhez: python + playwright (+ chromium), node.
# A tesztek ideiglenes mappában dolgoznak; egy valódi agent beállításait nem érintik.
param([string]$Part = "all")
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
$Work = Join-Path ([IO.Path]::GetTempPath()) ("dtb-tests-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $Work | Out-Null
$env:DEVTOOLBOX_AGENT_DIR = Join-Path $Work "agent"
$SrvDll = "hosts\FleetServer.Host\bin\Release\net10.0\FleetServer.Host.dll"
$HubDll = "hosts\FleetHub.Host\bin\Release\net10.0\FleetHub.Host.dll"
$script:Pass = 0; $script:Fail = 0; $script:Skip = 0; $script:Failed = @()

function Ok($n)        { $script:Pass++; "  {0,-26} OK" -f $n }
function Bad($n, $out) { $script:Fail++; $script:Failed += $n; "  {0,-26} HIBA" -f $n
                         $lines = @("$out" -split "`n" | Where-Object { $_.Trim() })
                         $hit = @($lines | Select-String "HIBA|error|Exception|BUKOTT" | Select-Object -First 15)
                         # Ha nincs ilyen sor (pl. a tesztszerver el sem indult), az első sorok mennek ki - az ok így is látszik.
                         if ($hit.Count -eq 0) { $hit = @($lines | Select-Object -First 12) }
                         $hit | ForEach-Object { "      $_" } }
function Skip($n, $w)  { $script:Skip++; "  {0,-26} kihagyva ({1})" -f $n, $w }
function Check($n, [scriptblock]$cmd) { $out = (& $cmd 2>&1 | Out-String); if ($out -match "MINDEN TESZT ATMENT") { Ok $n } else { Bad $n $out } }
function Wait-Url($u) { for ($i = 0; $i -lt 80; $i++) { try { Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true } catch { Start-Sleep -Milliseconds 500 } }; $false }
function Start-TestHost($dll, $var, $dir, $cfg, $ping) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    if (-not (Test-Path "$dir\server-config.json")) { Set-Content "$dir\server-config.json" $cfg -Encoding ASCII }
    Set-Item "env:$var" $dir
    $p = Start-Process dotnet -ArgumentList "`"$dll`" run" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$dir\host.log" -RedirectStandardError "$dir\host.err"
    Remove-Item "env:$var"
    if (Wait-Url $ping) { return $p }
    Stop-Process $p -Force -ErrorAction SilentlyContinue
    # A szerver saját naplója mondja meg, miért nem indult (port foglalt, jogosultság, összeomlás).
    # Write-Host: ne kerüljön a visszatérési értékbe (a hívó azt nézi, indult-e).
    Write-Host "      a tesztszerver nem válaszolt ($ping), a naplója:"
    foreach ($f in "$dir\host.log", "$dir\host.err") { if (Test-Path $f) { Get-Content $f -Tail 12 | ForEach-Object { Write-Host "        $_" } } }
    $null
}
function Start-Srv($dir) { Start-TestHost $SrvDll "FLEET_DATA" $dir '{"AgentPort":19743,"WebPort":19380}' "http://localhost:19743/api/ping" }
function Start-Hub($dir) { Start-TestHost $HubDll "FLEET_HUB_DATA" $dir '{"WebPort":18090}' "http://localhost:18090/hub/ping" }
function Stop-TestHost($p) { if ($p) { Stop-Process $p -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 700 } }
function Fake-Agent($path, $size) { $b = New-Object byte[] $size; (New-Object Random).NextBytes($b); [IO.File]::WriteAllBytes($path, $b) }
function Run-E2E($t) { dotnet run -c Release --no-build --project "e2e\$t" }

"== Fordítás =="
foreach ($p in (Get-ChildItem hosts, unit, e2e -Directory | ForEach-Object { $_.FullName }) + @("ui\RenderCheck")) {
    $e = dotnet build -c Release $p -nologo -v q 2>&1 | Select-String " error " | Select-Object -First 3
    if ($e) { "  FORDÍTÁSI HIBA: $p"; $e | ForEach-Object { "      $_" }; exit 1 }
}
"  minden projekt lefordult"

if ($Part -in "all", "unit") {
    "== Logikai tesztek =="
    foreach ($d in Get-ChildItem unit -Directory) { Check "unit/$($d.Name)" { dotnet run -c Release --no-build --project $d.FullName } }
}

if ($Part -in "all", "e2e", "ui") {
    "== Élő tesztek (valódi szerver- és agentkód, HTTP) =="
    foreach ($t in "Monitoring", "Security", "Setup", "Management") {
        $D = "$Work\srv-$t"; New-Item -ItemType Directory -Force $D | Out-Null; Fake-Agent "$D\DevToolboxAgent.exe" 2000000
        $h = Start-Srv $D
        if (-not $h) { Bad "e2e/$t" "a FleetServer nem indult el (port foglalt? rendszergazdai PowerShell?)"; continue }
        if ($Part -ne "ui") { Check "e2e/$t" { Run-E2E $t } } else { Run-E2E $t | Out-Null }
        Stop-TestHost $h
    }
    $D = "$Work\srv-AgentUpdate"; New-Item -ItemType Directory -Force $D | Out-Null; Fake-Agent "$D\DevToolboxAgent.exe" 3000000
    $env:PKG_SHA = (Get-FileHash "$D\DevToolboxAgent.exe" -Algorithm SHA256).Hash
    $h = Start-Srv $D
    if ($h) { if ($Part -ne "ui") { Check "e2e/AgentUpdate" { Run-E2E AgentUpdate } }; Stop-TestHost $h } else { Bad "e2e/AgentUpdate" "nem indult" }
    foreach ($t in "HubAccess", "HubDial") {
        $h = Start-Hub "$Work\hub-$t"
        $env:FLEET_HUB_TEST_DIR = "$Work\hub-$t"   # a teszt a Központ fájljait is ellenőrzi (pl. a jelszó titkosítva van-e)
        if ($h) { if ($Part -ne "ui") { Check "e2e/$t" { Run-E2E $t } } else { Run-E2E $t | Out-Null }; Stop-TestHost $h }
        else { Bad "e2e/$t" "a Központ nem indult el" }
    }
}

if ($Part -in "all", "ui") {
    "== Felület =="
    # node a PATH-on, vagy a Laragon mellé telepített (ezen a gépen ott van)
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $node -and (Test-Path "C:\laragon\bin\nodejs\node-v22\node.exe")) { $node = "C:\laragon\bin\nodejs\node-v22\node.exe" }
    if ($node) {
        dotnet run -c Release --no-build --project ui\RenderCheck -- "$Work\render" | Out-Null
        Check "ui/szkriptek (node)" { & $node ui\check_scripts.js "$Work\render" }
    } else { Skip "ui/szkriptek" "nincs node" }
    python -c "import playwright" 2>$null
    if ($LASTEXITCODE -eq 0) {
        $h = Start-Srv "$Work\srv-Monitoring"; if ($h) { Check "ui/monitorozás+oldalsáv" { python ui\browser_monitoring.py }; Stop-TestHost $h }
        $h = Start-Srv "$Work\srv-Management"; if ($h) { Check "ui/gépkezelés" { python ui\browser_management.py }; Stop-TestHost $h }
        $h = Start-Hub "$Work\hub-HubDial"; if ($h) { Check "ui/Központ" { python ui\browser_hub.py }; Stop-TestHost $h }
    } else { Skip "ui/böngésző" "nincs python+playwright" }
}

""; "== Összesen: $($script:Pass) sikeres, $($script:Fail) hibás, $($script:Skip) kihagyva =="
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
if ($script:Fail -gt 0) { "   hibás: " + ($script:Failed -join ", "); exit 1 }
