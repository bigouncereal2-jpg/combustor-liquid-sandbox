# Builds Liquid Sandbox, runs the solver checks and writes the release ZIPs to dist\.
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-GameDirectory "C:\...\EngineSimulator"] [-Install]
param(
    [string]$GameDirectory = "C:\Program Files (x86)\Steam\steamapps\common\EngineSimulator",
    [switch]$Install
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$version = "1.2.0"

if (-not (Test-Path (Join-Path $GameDirectory "MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll"))) {
    throw "No MelonLoader game bindings under $GameDirectory. Install MelonLoader, start Combustor once, or pass -GameDirectory."
}

dotnet build (Join-Path $root "src\LiquidSandbox\LiquidSandbox.csproj") -c Release -p:GameDirectory="$GameDirectory"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

dotnet run --project (Join-Path $root "tests\LiquidSandbox.Tests") -c Release
if ($LASTEXITCODE -ne 0) { throw "Solver checks failed." }

$dll = Join-Path $root "src\LiquidSandbox\bin\Release\net6.0\LiquidSandbox.dll"
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "Mods") | Out-Null
Copy-Item $dll (Join-Path $stage "Mods\LiquidSandbox.dll")
Copy-Item (Join-Path $root "INSTALL.txt") $stage

$zip = Join-Path $dist "LiquidSandbox-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip

$srcStage = Join-Path $dist "source\LiquidSandbox-$version-source"
if (Test-Path (Join-Path $dist "source")) { Remove-Item (Join-Path $dist "source") -Recurse -Force }
New-Item -ItemType Directory -Force $srcStage | Out-Null
foreach ($item in "README.md", "INSTALL.txt", "LICENSE", "build.ps1", ".gitignore") { Copy-Item (Join-Path $root $item) $srcStage }
foreach ($dir in "src", "tests", "tools") {
    Get-ChildItem (Join-Path $root $dir) -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $target = Join-Path $srcStage ($_.FullName.Substring($root.Length + 1))
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $_.FullName $target
        }
}
$srcZip = Join-Path $dist "LiquidSandbox-$version-source.zip"
if (Test-Path $srcZip) { Remove-Item $srcZip -Force }
Compress-Archive -Path $srcStage -DestinationPath $srcZip
Remove-Item $stage, (Join-Path $dist "source") -Recurse -Force

if ($Install) {
    Copy-Item $dll (Join-Path $GameDirectory "Mods\LiquidSandbox.dll") -Force
    Write-Host "Installed to $GameDirectory\Mods"
}
Write-Host "Wrote $zip"
Write-Host "Wrote $srcZip"
