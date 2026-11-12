# Builds RealFabricPreview.dll with the C# compiler that ships with Windows (.NET Framework 4).
# No SDK needed. References are taken from your own Dressmaker install (with BepInEx 5 installed).
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Game "D:\SteamLibrary\steamapps\common\Dressmaker"
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Package -BepInExZip "C:\path\BepInEx_win_x64_5.4.23.6.zip"
param(
  [string]$Game = "C:\Program Files (x86)\Steam\steamapps\common\Dressmaker",
  [switch]$Package,            # also create dist\RealFabricPreview_v<version>.zip
  [string]$BepInExZip = ""     # BepInEx 5 x64 zip to bundle in the "full install" folder
)

$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$managed = Join-Path $Game "Dressmaker_Data\Managed"
$bepCore = Join-Path $Game "BepInEx\core"
$out     = Join-Path $here "build\RealFabricPreview"
$csc     = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $managed)) { throw "Dressmaker not found at '$Game'. Use -Game <path to the game folder>." }
if (-not (Test-Path "$bepCore\BepInEx.dll")) { throw "BepInEx 5 is not installed in '$Game' (BepInEx\core\BepInEx.dll missing)." }
if (-not (Test-Path $csc)) { throw "csc.exe not found ($csc). It ships with Windows / .NET Framework 4." }

New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
  "$bepCore\BepInEx.dll", "$bepCore\0Harmony.dll",
  "$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll", "$managed\netstandard.dll",
  "$managed\Assembly-CSharp.dll", "$managed\Framework.dll", "$managed\Unity.TextMeshPro.dll",
  "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll",
  "$managed\UnityEngine.UI.dll", "$managed\UnityEngine.UIModule.dll",
  "$managed\UnityEngine.InputLegacyModule.dll", "$managed\UnityEngine.TextRenderingModule.dll",
  "$managed\UnityEngine.IMGUIModule.dll", "$managed\UnityEngine.ImageConversionModule.dll"
) | ForEach-Object { "/r:`"$_`"" }

& $csc /nologo /target:library /optimize+ /nostdlib+ /noconfig "/out:$out\RealFabricPreview.dll" @refs "$here\src\RealFabricPreview.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "OK -> $out\RealFabricPreview.dll"

if (-not $Package) { return }

# ---------------------------------------------------------------- release zip
$version = ([regex]::Match((Get-Content "$here\src\RealFabricPreview.cs" -Raw), 'Version = "([0-9.]+)"')).Groups[1].Value
$stage = Join-Path $here "dist\stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$full = Join-Path $stage "1 - Full install (Instalacion completa)"
$only = Join-Path $stage "2 - Mod only (Solo el mod)"
New-Item -ItemType Directory -Force "$full\BepInEx\plugins\RealFabricPreview", "$only\BepInEx\plugins\RealFabricPreview" | Out-Null

if ($BepInExZip) {
  if (-not (Test-Path $BepInExZip)) { throw "BepInEx zip not found: $BepInExZip" }
  Expand-Archive $BepInExZip $full -Force
} else {
  Write-Warning "No -BepInExZip given: the 'Full install' folder will only contain the mod."
}
Copy-Item "$out\RealFabricPreview.dll" "$full\BepInEx\plugins\RealFabricPreview\"
Copy-Item "$out\RealFabricPreview.dll" "$only\BepInEx\plugins\RealFabricPreview\"
Copy-Item "$here\release\*.txt" $stage
Copy-Item "$here\LICENSE" "$stage\LICENSE.txt"
Copy-Item "$here\THIRD_PARTY_NOTICES.md" "$stage\THIRD_PARTY_NOTICES.md"

$zip = Join-Path $here "dist\RealFabricPreview_v$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force
Write-Host "Package -> $zip"
