param(
    [switch]$RebuildNative,
    [string]$DeliriumRepo = "C:\Git\delirium-physics-no-game",
    [string]$RavenfieldDir = "C:\Program Files (x86)\Steam\steamapps\common\Ravenfield"
)

$ErrorActionPreference = "Stop"

$nativeDll = Join-Path $DeliriumRepo "target\x86_64-pc-windows-gnu\release\delirium_physics.dll"
$cargoBin = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"

if ($RebuildNative -or -not (Test-Path $nativeDll)) {
    Write-Host "=== Building delirium_physics.dll (Rust x86_64-pc-windows-gnu) ===" -ForegroundColor Cyan
    if (-not (Test-Path $cargoBin)) {
        throw "cargo.exe not found at $cargoBin. Please install Rustup with stable-x86_64-pc-windows-gnu."
    }
    Push-Location $DeliriumRepo
    try {
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        $machPath = [Environment]::GetEnvironmentVariable("Path", "Machine")
        $env:PATH = "$userPath;$machPath"
        $env:RUSTFLAGS = "-C target-cpu=x86-64-v3"
        & $cargoBin build --release -p delirium-sdk --target x86_64-pc-windows-gnu
        if ($LASTEXITCODE -ne 0) { throw "Cargo build failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

Write-Host "=== Building DeliriumRavenfield.dll (.NET 4.7.1) ===" -ForegroundColor Cyan
& dotnet build (Join-Path $PSScriptRoot "DeliriumRavenfield.csproj") -c Release "-p:RavenfieldDir=$RavenfieldDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$modDll = Join-Path $PSScriptRoot "bin\Release\net471\DeliriumRavenfield.dll"
$pluginsDir = Join-Path $RavenfieldDir "BepInEx\plugins"
$destDir = Join-Path $pluginsDir "DeliriumRavenfield"

# Disable WeroRagdoll.dll (backup as .disabled) so ragdoll hooks don't conflict
$weroMatches = Get-ChildItem -Path $pluginsDir -Filter "*Wero*Ragdoll*.dll" -Recurse -ErrorAction SilentlyContinue
foreach ($w in $weroMatches) {
    $disabledPath = $w.FullName + ".disabled"
    Write-Host "Disabling conflicting plugin: $($w.FullName) -> $disabledPath" -ForegroundColor Yellow
    Move-Item -Force $w.FullName $disabledPath
}

New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Copy-Item -Force $modDll (Join-Path $destDir "DeliriumRavenfield.dll")
Copy-Item -Force $nativeDll (Join-Path $destDir "delirium_physics.dll")
$getupNet = Join-Path $DeliriumRepo "getup.net"
if (Test-Path $getupNet) {
    Copy-Item -Force $getupNet (Join-Path $destDir "getup.net")
}

Write-Host "=== Deployed successfully to $destDir ===" -ForegroundColor Green
Get-ChildItem $destDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
