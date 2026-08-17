# fetch-refs.ps1 — collect the compile-time reference DLLs into References\ from PUBLIC sources.
#
# Who this is for: CI (GitHub Actions has no Humankind install) and anyone building the plugin without the game.
# A modder with the game installed does NOT need this — copying the DLLs from the install (docs/Building.md) stays
# the primary path, and this script never overwrites a DLL that is already present (game copies win; -Force to redo).
#
# Everything fetched here is publicly redistributed by its own project — no game files:
#   Newtonsoft.Json 11.0.1    nuget.org                      (version the game ships in Humankind_Data/Managed)
#   BepInEx.dll + 0Harmony    BepInEx v5.4.21 release zip    (github.com/BepInEx/BepInEx — the modder-installed loader)
#   UnityEngine.* modules     unity.bepinex.dev 2021.3.1     (BepInEx's public mirror of RUNNABLE unstripped Unity
#                                                             assemblies, version-exact to the game; the nuget
#                                                             UnityEngine.Modules package is compile-only reference
#                                                             assemblies — tests throw TypeLoadException on them)
# The game's own Amplitude.Mercury.Animation.dll is NOT needed: the plugin's only compile-time game surface is
# string-based reflection (the csproj reference was removed 2026-08-17 — see CHANGELOG).
#
# Usage:  pwsh tools/fetch-refs.ps1 [-Dest <dir>] [-Force]
# Exits non-zero if any required DLL is missing at the end (fail loud, never a partial silent success).
param(
    [string]$Dest = (Join-Path (Split-Path $PSScriptRoot -Parent) "References"),
    [switch]$Force
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$required = @(
    "Newtonsoft.Json.dll", "BepInEx.dll", "0Harmony.dll",
    "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll", "Mono.Cecil.dll",   # Harmony's own runtime deps — the test suite EXECUTES patches (shared-seam census tests)
    "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.IMGUIModule.dll",
    "UnityEngine.InputLegacyModule.dll", "UnityEngine.JSONSerializeModule.dll",
    "UnityEngine.ImageConversionModule.dll", "UnityEngine.AudioModule.dll",
    "UnityEngine.PhysicsModule.dll", "UnityEngine.AssetBundleModule.dll"
)

New-Item -ItemType Directory -Force $Dest | Out-Null
$missing = $required | Where-Object { $Force -or -not (Test-Path (Join-Path $Dest $_)) }
if (-not $missing) { Write-Host "References complete at $Dest - nothing to fetch."; exit 0 }
Write-Host "Fetching $($missing.Count) missing reference DLL(s) -> $Dest"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("haf-refs-" + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    function Get-Zip([string]$url, [string]$name) {
        $zip = Join-Path $tmp "$name.zip"; $out = Join-Path $tmp $name
        Write-Host "  download $url"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath $out -Force
        return $out
    }
    function Place([string]$src, [string]$name) {
        if (-not (Test-Path $src)) { throw "expected file not in package: $src" }
        Copy-Item $src (Join-Path $Dest $name) -Force
        Write-Host "  placed $name"
    }

    if ($missing -contains "Newtonsoft.Json.dll") {
        $p = Get-Zip "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/11.0.1/newtonsoft.json.11.0.1.nupkg" "newtonsoft"
        Place (Join-Path $p "lib\net45\Newtonsoft.Json.dll") "Newtonsoft.Json.dll"
    }
    $fromBepInEx = @("BepInEx.dll", "0Harmony.dll", "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll", "Mono.Cecil.dll")
    if ($missing | Where-Object { $fromBepInEx -contains $_ }) {
        $p = Get-Zip "https://github.com/BepInEx/BepInEx/releases/download/v5.4.21/BepInEx_x64_5.4.21.0.zip" "bepinex"
        foreach ($dll in $fromBepInEx) { if ($missing -contains $dll) { Place (Join-Path $p "BepInEx\core\$dll") $dll } }
    }
    $unityMissing = $missing | Where-Object { $_ -like "UnityEngine*" }
    if ($unityMissing) {
        $p = Get-Zip "https://unity.bepinex.dev/libraries/2021.3.1.zip" "unity"
        foreach ($dll in $unityMissing) { Place (Join-Path $p $dll) $dll }
    }
}
finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }

$still = $required | Where-Object { -not (Test-Path (Join-Path $Dest $_)) }
if ($still) { Write-Error "FETCH INCOMPLETE - still missing: $($still -join ', ')"; exit 1 }
Write-Host "OK - all $($required.Count) reference DLLs present at $Dest"
