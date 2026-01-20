param(
    [string]$ItemsPath = (Join-Path $PSScriptRoot "..\Resources\ArcRaidersItems.json"),
    [string]$OutputRecyclePath = (Join-Path $PSScriptRoot "..\Resources\ArcRaidersRecycleValues.json"),
    [string]$OutputCraftingPath = (Join-Path $PSScriptRoot "..\Resources\ArcRaidersCraftingKeep.json"),
    [int]$DelayMs = 200
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ItemsPath)) {
    throw "Items JSON not found at $ItemsPath"
}

$items = Get-Content -Path $ItemsPath -Raw | ConvertFrom-Json

$existingRecycle = @{}
if (Test-Path $OutputRecyclePath) {
    try {
        $existing = Get-Content -Path $OutputRecyclePath -Raw | ConvertFrom-Json
        foreach ($prop in $existing.PSObject.Properties) {
            $existingRecycle[$prop.Name] = [int]$prop.Value
        }
    } catch { }
}

$existingCrafting = @{}
if (Test-Path $OutputCraftingPath) {
    try {
        $existing = Get-Content -Path $OutputCraftingPath -Raw | ConvertFrom-Json
        foreach ($prop in $existing.PSObject.Properties) {
            $existingCrafting[$prop.Name] = [string]$prop.Value
        }
    } catch { }
}

$recycleMap = @{}
foreach ($key in $existingRecycle.Keys) { $recycleMap[$key] = $existingRecycle[$key] }

$craftingMap = @{}
foreach ($key in $existingCrafting.Keys) { $craftingMap[$key] = $existingCrafting[$key] }

foreach ($item in $items) {
    $id = $item.Id
    if ([string]::IsNullOrWhiteSpace($id)) { continue }

    try {
        $url = "https://metaforge.app/arc-raiders/database/item/$id/__data.json"
        $payload = Invoke-RestMethod -Uri $url -Method Get
        $data = $payload.nodes[2].data
        $cache = @{}

        function Resolve-Node($value) {
            if ($null -eq $value) { return $null }
            if ($value -is [int] -or $value -is [long]) {
                $index = [int]$value
                if ($index -ge 0 -and $index -lt $data.Count) {
                    if ($cache.ContainsKey($index)) { return $cache[$index] }
                    $cache[$index] = $null
                    $resolved = Resolve-Node $data[$index]
                    $cache[$index] = $resolved
                    return $resolved
                }
                return $value
            }
            if ($value -is [double] -or $value -is [string] -or $value -is [bool]) { return $value }
            if ($value -is [psobject]) {
                $obj = @{}
                foreach ($prop in $value.PSObject.Properties) {
                    $obj[$prop.Name] = Resolve-Node $prop.Value
                }
                return $obj
            }
            if ($value -is [System.Collections.IEnumerable]) {
                $arr = @()
                foreach ($itemValue in $value) {
                    $arr += Resolve-Node $itemValue
                }
                return $arr
            }
            return $value
        }

        $resolved = Resolve-Node $data[0]

        $totalRecycleValue = [int]($resolved.totalRecycleValue ?? 0)
        if ($totalRecycleValue -gt 0) {
            $recycleMap[$id] = $totalRecycleValue
        }

        $usedInCount = 0
        if ($resolved.item -and $resolved.item.used_in) {
            $usedInCount = $resolved.item.used_in.Count
        }
        if ($usedInCount -gt 0) {
            $craftingMap[$id] = "Used in $usedInCount recipes"
        }

        Start-Sleep -Milliseconds $DelayMs
    } catch {
        Write-Warning "Failed to load $id: $($_.Exception.Message)"
    }
}

$recycleOut = [ordered]@{}
foreach ($key in ($recycleMap.Keys | Sort-Object)) { $recycleOut[$key] = $recycleMap[$key] }
$recycleOut | ConvertTo-Json -Depth 3 | Set-Content -Path $OutputRecyclePath -Encoding UTF8

$craftOut = [ordered]@{}
foreach ($key in ($craftingMap.Keys | Sort-Object)) { $craftOut[$key] = $craftingMap[$key] }
$craftOut | ConvertTo-Json -Depth 3 | Set-Content -Path $OutputCraftingPath -Encoding UTF8

Write-Host "Wrote recycle values to $OutputRecyclePath"
Write-Host "Wrote crafting keep list to $OutputCraftingPath"