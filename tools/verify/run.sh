#!/usr/bin/env bash
# Compiles ninjatrader/Strategies/RutaCryptoMirror.cs as C# 5 against NinjaTrader stubs, runs it on
# synthetic 5-minute data (several seeds) and cross-checks every bar and trade against an
# independent Python implementation of the Pine script. Needs: dotnet SDK 8, python3.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build "$here/Verify.csproj" -c Release -o "$work/bin" -nologo -v quiet
for seed in 12345 1 7 42 99 2026; do
  mkdir -p "$work/$seed"
  echo "== seed $seed"
  dotnet "$work/bin/Verify.dll" "$work/$seed" "$seed" | grep -E "Summary|FAIL|ALL"
  python3 "$here/pine_reference.py" "$work/$seed" | tail -3
done
