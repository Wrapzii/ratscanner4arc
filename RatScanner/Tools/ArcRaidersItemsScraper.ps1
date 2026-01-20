param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\Resources\ArcRaidersItems.json"),
    [int]$Limit = 100
)

$ErrorActionPreference = "Stop"

$baseUrl = "https://metaforge.app/api/arc-raiders/items"
$page = 1
$totalPages = 1
$all = @()

$existingRecycleMap = @{}
if (Test-Path $OutputPath) {
    try {
        $existingItems = Get-Content -Path $OutputPath -Raw | ConvertFrom-Json
        foreach ($existing in $existingItems) {
            if ($null -ne $existing.Id -and $null -ne $existing.RecycleValue) {
                $existingRecycleMap[$existing.Id] = [int]$existing.RecycleValue
            }
        }
    } catch { }
}

$overrideMap = @{}
$overridePath = Join-Path $PSScriptRoot "..\Resources\ArcRaidersRecycleValues.json"
if (Test-Path $overridePath) {
    try {
        $overrideData = Get-Content -Path $overridePath -Raw | ConvertFrom-Json
        foreach ($prop in $overrideData.PSObject.Properties) {
            $overrideMap[$prop.Name] = [int]$prop.Value
        }
    } catch { }
}

$dir = Split-Path $OutputPath
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir | Out-Null
}

while ($page -le $totalPages) {
    $builder = [System.UriBuilder]$baseUrl
    $builder.Query = "page=$page&limit=$Limit&sortOrder=asc"
    $uri = $builder.Uri.AbsoluteUri
    $response = Invoke-RestMethod -Uri $uri -Method Get

    if (-not $response -or -not $response.data) {
        break
    }

    foreach ($item in $response.data) {
        $category = if ($item.item_type) { $item.item_type } else { "General" }
        $isRecyclable = $category -ieq "Recyclable"
        $rarity = if ($item.rarity) { $item.rarity } else { "" }
        $weight = 0.0
        if ($item.stat_block -and $null -ne $item.stat_block.weight) {
            $weight = [double]$item.stat_block.weight
        }

        $recycleValue = 0
        if ($overrideMap.ContainsKey($item.id)) {
            $recycleValue = $overrideMap[$item.id]
        } elseif ($existingRecycleMap.ContainsKey($item.id)) {
            $recycleValue = $existingRecycleMap[$item.id]
        } elseif ($null -ne $item.recycle_value) {
            $recycleValue = [int]$item.recycle_value
        }

        $all += [pscustomobject]@{
            Id = $item.id
            Name = $item.name
            ShortName = $item.name
            Width = 1
            Height = 1
            Value = [int]$item.value
            RecycleValue = $recycleValue
            IsQuestItem = $false
            IsBaseItem = $false
            IsRecyclable = $isRecyclable
            Rarity = $rarity
            Weight = $weight
            ImageLink = $item.icon
            WikiLink = "https://metaforge.app/arc-raiders/database/item/$($item.id)"
            Category = $category
        }
    }

    $totalPages = $response.pagination.totalPages
    $page++
}

$all | Sort-Object Name | ConvertTo-Json -Depth 4 | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host "Wrote $($all.Count) items to $OutputPath"