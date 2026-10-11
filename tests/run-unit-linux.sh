#!/usr/bin/env bash
# Keresztplatformos unit tesztek futtatása Linuxon (dotnet 10). A Windows-only
# részek (GUI/WMI/P-Invoke) ebben nincsenek benne - azok Windowson, a run-tests.ps1-gyel.
set -u
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$(dirname "$0")/unit" || exit 1
pass=0; fail=0; failed=""
for d in */ ; do
  name="${d%/}"
  [ -f "$d"*.csproj ] || continue
  out="$(cd "$d" && timeout 300 dotnet run -c Release 2>&1)"
  if echo "$out" | grep -q "MINDEN TESZT ATMENT"; then
    printf "  %-16s OK\n" "$name"; pass=$((pass+1))
  else
    printf "  %-16s HIBA\n" "$name"; fail=$((fail+1)); failed="$failed $name"
    echo "$out" | grep -iE "HIBA|error|Exception|BUKOTT" | head -8 | sed 's/^/      /'
  fi
done
echo "-----"; echo "Átment: $pass  Bukott: $fail"
[ -n "$failed" ] && echo "Bukott tesztek:$failed"
