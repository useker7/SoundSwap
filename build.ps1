# Builds the Windows app and the Android APK into dist\.
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

# --- Windows (.NET 8) ---
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userSdk = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet-sdk\dotnet.exe"
if (Test-Path $userSdk) { $dotnet = $userSdk }
& $dotnet test "$root\windows\SoundSwap.Tests\SoundSwap.Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Windows unit tests failed" }
& $dotnet publish "$root\windows\SoundSwap\SoundSwap.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o "$root\windows\publish"
if ($LASTEXITCODE -ne 0) { throw "Windows build failed" }
Copy-Item "$root\windows\publish\SoundSwap.exe" "$dist\SoundSwap.exe" -Force

# --- Android ---
if (-not $env:JAVA_HOME -and (Test-Path "C:\Program Files\Android\Android Studio\jbr")) {
    $env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
}
Push-Location "$root\android"
try {
    .\gradlew.bat testReleaseUnitTest assembleRelease --console=plain
    if ($LASTEXITCODE -ne 0) { throw "Android build failed" }
} finally { Pop-Location }
Copy-Item "$root\android\app\build\outputs\apk\release\app-release.apk" "$dist\SoundSwap.apk" -Force

Write-Host "`nDone:" -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB) }
