$ErrorActionPreference = "Stop"

# Configuration
$projectPath = Join-Path $PSScriptRoot "..\RatScanner.csproj"
$publishDir = Join-Path $PSScriptRoot "..\bin\Release\publish"
$packId = "RatScanner"
$mainExe = "RatScanner.exe"

# 1. Install/Update vpk
Write-Host "Checking for vpk..." -ForegroundColor Cyan
try {
    dotnet tool update -g vpk
} catch {
    dotnet tool install -g vpk
}

# 2. Get Version from csproj
$xml = [xml](Get-Content $projectPath)
$version = $xml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = "1.0.0"
}
Write-Host "Detected Version: $version" -ForegroundColor Cyan

# 3. Publish
Write-Host "Publishing Application..." -ForegroundColor Cyan
dotnet publish $projectPath -c Release --self-contained -r win-x64 -o $publishDir

# 4. Pack
Write-Host "Packing Release..." -ForegroundColor Cyan
# vpk pack --packId $packId --packVersion $version --packDir $publishDir --mainExe $mainExe
vpk pack --packId $packId --packVersion $version --packDir $publishDir --mainExe $mainExe

Write-Host "Done! Releases are in the Releases folder." -ForegroundColor Green
