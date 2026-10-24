param([string]$UnityPath)
$ErrorActionPreference = 'Stop'
$projectPath = $PSScriptRoot
$versionLine = Get-Content -LiteralPath (Join-Path $projectPath 'ProjectSettings\ProjectVersion.txt') | Select-Object -First 1
$projectVersion = ($versionLine -split ':', 2)[1].Trim()
if (-not $UnityPath) {
    $candidates = @("D:\Program Files\$projectVersion\Editor\Unity.exe", "D:\Program Files\Unity\Hub\Editor\$projectVersion\Editor\Unity.exe")
    $hubConfig = Join-Path $env:APPDATA 'UnityHub\editors-v2.json'
    if (Test-Path -LiteralPath $hubConfig) {
        $editors = (Get-Content -Raw -LiteralPath $hubConfig | ConvertFrom-Json).data
        $candidates += @($editors | Where-Object version -EQ $projectVersion | ForEach-Object { $_.location })
    }
    $UnityPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $UnityPath -or -not (Test-Path -LiteralPath $UnityPath)) {
    throw "Unity $projectVersion not found. Run this script with -UnityPath <path to Unity.exe>, or add this folder in Unity Hub."
}
Start-Process -FilePath $UnityPath -ArgumentList @('-projectPath', ('"' + $projectPath + '"'), '-executeMethod', 'SceneScope.Editor.SceneScopeShowcase.Open')
