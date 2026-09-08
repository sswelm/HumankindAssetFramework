# editor_tests.ps1 - run the in-editor INTEGRATION suites headless (BakeFeatureTest Tier 1 via
# HeadlessBakeTests.Run). The opt-in lane for baker changes: a Unity boot costs about a minute, so this is
# NOT in the per-push gate (and hosted CI has no licensed Unity). Run it before merging anything that touches
# editor/UniversalBaker.cs or the bake pipeline - the automated form of Factory-Manual section 11's
# "run the bake tests before committing baker changes".
#
#   powershell -File tools/editor_tests.ps1                      # defaults below
#   powershell -File tools/editor_tests.ps1 -Project D:\MyMod    # another modding project with the HAF package
#
# Overrides: -Project / -Unity parameters, or HAF_UNITY_PROJECT / HAF_UNITY environment variables.
# Requires: the target project resolves the HAF authoring package (this repo's editor/) and is NOT currently
# open in the Unity editor (batch mode refuses a locked project).
#
# NOTE this file must stay pure ASCII: PowerShell 5.1 reads a BOM-less .ps1 as ANSI, and the cp1252 bytes of
# a UTF-8 em-dash include a curly quote that terminates strings mid-line (learned 2026-09-08).
param(
    [string]$Project = $(if ($env:HAF_UNITY_PROJECT) { $env:HAF_UNITY_PROJECT } else { "C:\Repo\ENCReload" }),
    [string]$Unity   = $(if ($env:HAF_UNITY) { $env:HAF_UNITY } else { "C:\Program Files\Unity 2021.3.1f1\Editor\Unity.exe" })
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Unity)) { Write-Host "FAIL: Unity not found at '$Unity' (set -Unity or HAF_UNITY)"; exit 2 }
if (-not (Test-Path (Join-Path $Project "Assets"))) { Write-Host "FAIL: '$Project' is not a Unity project (set -Project or HAF_UNITY_PROJECT)"; exit 2 }
if (Test-Path (Join-Path $Project "Temp\UnityLockfile")) {
    Write-Host "FAIL: '$Project' appears to be open in the Unity editor - close it first (batch mode refuses a locked project)."
    exit 2
}

$log = Join-Path ([IO.Path]::GetTempPath()) "haf_editor_tests.log"
Write-Host "=== headless bake feature tests (Unity batch, about 1 min boot + bakes; log: $log) ==="

& $Unity -batchmode -nographics -projectPath $Project -executeMethod HeadlessBakeTests.Run -logFile $log | Out-Null
$code = $LASTEXITCODE

# Surface the suite's own report lines from the Unity log.
if (Test-Path $log) {
    Get-Content $log | Select-String -Pattern "\[HeadlessBakeTests\]|^(PASS|FAIL|SKIP):" | ForEach-Object { $_.Line }
}

if ($code -eq 0) { Write-Host "[PASS] headless bake feature tests" }
else { Write-Host "[FAIL] headless bake feature tests (exit $code) - full log: $log" }
exit $code
