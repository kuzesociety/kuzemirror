#!/usr/bin/env bash
# Compiles ninjatrader/Strategies/RutaCryptoMirror.cs (+ the Prop Account Manager router) as C# 5 against
# NinjaTrader stubs, runs it on synthetic 5-minute data (several seeds), cross-checks every bar and trade
# against an independent Python implementation of the Pine script, and runs the router checks against a
# simulated broker. Then compiles every shipped NinjaScript file together against the real WPF reference
# assemblies (downloaded from NuGet on the first run). Needs: dotnet SDK 8, python3.
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
echo "== compile check: all strategies + Prop Account Manager add-on, real WPF"
dotnet build "$here/compilecheck/CompileCheck.csproj" -c Release -o "$work/cc" -nologo -v quiet | grep -E "error|Build succeeded" || true
test -f "$work/cc/CompileCheck.dll" && echo "COMPILE CHECK PASSED"
