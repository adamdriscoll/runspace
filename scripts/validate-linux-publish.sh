#!/usr/bin/env bash
set -euo pipefail

payload="$(realpath "$1")"
results="$(realpath "$2")"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
cp -a "$payload" "$scratch/payload"
mkdir -p "$scratch/home" "$scratch/empty-path"
mkdir -p "$scratch/home/.config/powershell"
printf '%s\n' '$global:RunspaceProfileExecuted = $true; throw "Automatic profiles must not run."' >"$scratch/home/.config/powershell/profile.ps1"
chmod +x "$scratch/payload/Runspace.Desktop"
cd "$scratch/home"

probe() {
    local name="$1" expected="$2" error="${3:-}" status=0
    rm -f "$results/$name.json"
    /usr/bin/env -i HOME="$scratch/home" PATH="$scratch/empty-path" \
        PSModulePath="$scratch/empty-path" DOTNET_ROOT="$scratch/empty-path" \
        DOTNET_MULTILEVEL_LOOKUP=0 DISPLAY="$DISPLAY" XAUTHORITY="$XAUTHORITY" \
        /usr/bin/timeout 90 "$scratch/payload/Runspace.Desktop" \
        --validate-publish "$results/$name.json" >"$results/$name.log" 2>&1 || status=$?
    python3 - "$results/$name.json" "$expected" "$error" "$status" <<'PY'
import json, sys
with open(sys.argv[1]) as stream:
    report = json.load(stream)
expected, error, status = sys.argv[2], sys.argv[3], int(sys.argv[4])
if expected == "success":
    assert status == 0 and report["Passed"], report
else:
    assert status != 0 and not report["Passed"], report
    assert any(not check["Passed"] and error in check["Detail"] for check in report["Checks"]), report
print(f'{sys.argv[1]}: expected {expected}, exit {status}')
PY
}

probe published success
manifest="$scratch/payload/runtimes/unix/lib/net10.0/Modules/Microsoft.PowerShell.Management/Microsoft.PowerShell.Management.psd1"
mv "$manifest" "$manifest.removed"
probe missing-manifest failure Microsoft.PowerShell.Management.psd1
mv "$manifest.removed" "$manifest"
assembly="$scratch/payload/Microsoft.PowerShell.Commands.Utility.dll"
mv "$assembly" "$assembly.removed"
probe missing-assembly failure Microsoft.PowerShell.Commands.Utility.dll
mv "$assembly.removed" "$assembly"
native="$scratch/payload/libSkiaSharp.so"
mv "$native" "$native.removed"
probe missing-native failure SkiaSharp
