#!/usr/bin/env bash
# DevToolbox – minden teszt futtatása.   Használat: ./run-tests.sh [unit|e2e|ui|all]
# Kell: .NET 10 SDK, curl. A böngészős részhez: python3 + playwright (+ chromium), node.
set -u
cd "$(dirname "$0")"
PART="${1:-all}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORK="$(mktemp -d)"
export DEVTOOLBOX_AGENT_DIR="$WORK/agent"       # a tesztek sosem írnak egy valódi agent mappájába
SRV_DLL=hosts/FleetServer.Host/bin/Release/net10.0/FleetServer.Host.dll
HUB_DLL=hosts/FleetHub.Host/bin/Release/net10.0/FleetHub.Host.dll
PASS=0; FAIL=0; SKIP=0; FAILED=(); SRV_PID=""; HUB_PID=""

ok()   { PASS=$((PASS+1)); printf "  %-26s OK\n" "$1"; }
bad()  { FAIL=$((FAIL+1)); FAILED+=("$1"); printf "  %-26s HIBA\n" "$1"; echo "$2" | grep -E "HIBA|error|Exception|BUKOTT" | head -15 | sed 's/^/      /'; }
skip() { SKIP=$((SKIP+1)); printf "  %-26s kihagyva (%s)\n" "$1" "$2"; }
check(){ local n=$1; shift; local out; out=$("$@" 2>&1); if echo "$out" | grep -q "MINDEN TESZT ATMENT"; then ok "$n"; else bad "$n" "$out"; fi; }
build(){ local e; e=$(dotnet build -c Release "$1" -nologo -v q 2>&1 | grep -E " error " | sort -u | head -3); [ -z "$e" ] || { echo "  FORDÍTÁSI HIBA: $1"; echo "$e" | sed 's/^/      /'; return 1; }; }
wait_url(){ for _ in $(seq 1 80); do curl -s -o /dev/null "$1" && return 0; sleep 0.5; done; return 1; }
start_server(){ mkdir -p "$1"; [ -f "$1/server-config.json" ] || echo '{"AgentPort":19743,"WebPort":19380}' > "$1/server-config.json"
  FLEET_DATA="$1" dotnet "$SRV_DLL" run > "$1/host.log" 2>&1 & SRV_PID=$!; wait_url http://localhost:19743/api/ping; }
stop_server(){ [ -n "$SRV_PID" ] && { kill $SRV_PID 2>/dev/null; wait $SRV_PID 2>/dev/null; SRV_PID=""; sleep 0.5; }; }
start_hub(){ mkdir -p "$1"; [ -f "$1/server-config.json" ] || echo '{"WebPort":18090}' > "$1/server-config.json"
  FLEET_HUB_DATA="$1" dotnet "$HUB_DLL" run > "$1/host.log" 2>&1 & HUB_PID=$!; wait_url http://localhost:18090/hub/ping; }
stop_hub(){ [ -n "$HUB_PID" ] && { kill $HUB_PID 2>/dev/null; wait $HUB_PID 2>/dev/null; HUB_PID=""; sleep 0.5; }; }
trap 'stop_server; stop_hub; rm -rf "$WORK"' EXIT
run_e2e(){ dotnet run -c Release --no-build --project "e2e/$1"; }
HAVE_PW=0; python3 -c "import playwright" 2>/dev/null && HAVE_PW=1

echo "== Fordítás =="
for p in hosts/*/ unit/*/ e2e/*/ ui/RenderCheck/; do build "$p" || exit 1; done
echo "  minden projekt lefordult"

if [ "$PART" = all ] || [ "$PART" = unit ]; then
  echo "== Logikai tesztek =="
  for d in unit/*/; do n=$(basename "$d"); check "unit/$n" dotnet run -c Release --no-build --project "$d"; done
fi

if [ "$PART" = all ] || [ "$PART" = e2e ] || [ "$PART" = ui ]; then
  echo "== Élő tesztek (valódi szerver- és agentkód, HTTP) =="
  for t in Monitoring Security Setup Management; do
    D="$WORK/srv-$t"; mkdir -p "$D"; head -c 2000000 /dev/urandom > "$D/DevToolboxAgent.exe"
    start_server "$D" || { bad "e2e/$t" "a FleetServer nem indult el (port foglalt?)"; continue; }
    if [ "$PART" != ui ]; then check "e2e/$t" run_e2e $t; else run_e2e $t >/dev/null 2>&1; fi
    stop_server
  done
  D="$WORK/srv-AgentUpdate"; mkdir -p "$D"; head -c 3000000 /dev/urandom > "$D/DevToolboxAgent.exe"
  export PKG_SHA=$(sha256sum "$D/DevToolboxAgent.exe" | cut -d' ' -f1 | tr a-f A-F)
  if start_server "$D"; then [ "$PART" != ui ] && check "e2e/AgentUpdate" run_e2e AgentUpdate; stop_server; else bad "e2e/AgentUpdate" "nem indult"; fi
  for t in HubAccess HubDial; do
    D="$WORK/hub-$t"; export FLEET_HUB_TEST_DIR="$D"
    if start_hub "$D"; then [ "$PART" != ui ] && check "e2e/$t" run_e2e $t || { [ "$PART" = ui ] && run_e2e $t >/dev/null 2>&1; }; stop_hub; else bad "e2e/$t" "a Központ nem indult el"; fi
  done
fi

if [ "$PART" = all ] || [ "$PART" = ui ]; then
  echo "== Felület =="
  if command -v node >/dev/null; then
    dotnet run -c Release --no-build --project ui/RenderCheck -- "$WORK/render" >/dev/null 2>&1
    check "ui/szkriptek (node)" node ui/check_scripts.js "$WORK/render"
  else skip "ui/szkriptek" "nincs node"; fi
  if [ $HAVE_PW = 1 ]; then
    start_server "$WORK/srv-Monitoring" && { check "ui/monitorozás+oldalsáv" python3 ui/browser_monitoring.py; stop_server; }
    start_server "$WORK/srv-Management" && { check "ui/gépkezelés" python3 ui/browser_management.py; stop_server; }
    start_hub "$WORK/hub-HubDial" && { check "ui/Központ" python3 ui/browser_hub.py; stop_hub; }
  else skip "ui/böngésző" "nincs python3+playwright"; fi
fi

echo; echo "== Összesen: $PASS sikeres, $FAIL hibás, $SKIP kihagyva =="
[ $FAIL -eq 0 ] || { printf '   hibás: %s\n' "${FAILED[@]}"; exit 1; }
