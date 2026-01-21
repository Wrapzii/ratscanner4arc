param (
    [string]$SetVersion,
    [switch]$AutoIncrement
)

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

# Function to get the correct PropertyGroup
function Get-VersionPropertyGroup {
    param($ProjectXml)
    $groups = $ProjectXml.Project.PropertyGroup
    foreach ($group in $groups) {
        if ($group.Version) {
            return $group
        }
    }
    # Fallback to first group if no version found
    if ($groups -is [array]) { return $groups[0] }
    return $groups
}

$propGroup = Get-VersionPropertyGroup -ProjectXml $xml
$currentVersion = $propGroup.Version

if ([string]::IsNullOrWhiteSpace($currentVersion)) {
    $currentVersion = "1.0.0"
}

if ($SetVersion) {
    $version = $SetVersion
    Write-Host "Setting Version to: $version" -ForegroundColor Cyan
    $propGroup.Version = $version
    $xml.Save($projectPath)
}
elseif ($AutoIncrement) {
    # Parse version
    try {
        $v = [version]$currentVersion
        # Increment Build (3rd digit)
        $newV = [version]::new($v.Major, $v.Minor, $v.Build + 1)
        $version = $newV.ToString()
        
        Write-Host "Auto-Incrementing Version: $currentVersion -> $version" -ForegroundColor Cyan
        $propGroup.Version = $version
        $xml.Save($projectPath)
    }
    catch {
        Write-Warning "Could not parse or increment version '$currentVersion'. using unmodified."
        $version = $currentVersion
    }
}
else {
    $version = $currentVersion
    Write-Host "Using Current Version: $version" -ForegroundColor Cyan
}

# 3. Publish
Write-Host "Publishing Application..." -ForegroundColor Cyan
dotnet publish $projectPath -c Release --self-contained -r win-x64 -o $publishDir

# 4. Pack
Write-Host "Packing Release..." -ForegroundColor Cyan
# vpk pack --packId $packId --packVersion $version --packDir $publishDir --mainExe $mainExe
vpk pack --packId $packId --packVersion $version --packDir $publishDir --mainExe $mainExe

Write-Host "Done! Releases are in the Releases folder." -ForegroundColor Green
Write-Host "Please upload the following files to your GitHub Release:" -ForegroundColor Yellow
Get-ChildItem -Path "Releases" | ForEach-Object { Write-Host " - $($_.Name)" }
Write-Host "IMPORTANT: Do NOT rename these files. Upload them exactly as they are." -ForegroundColor Red
