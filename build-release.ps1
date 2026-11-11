<#
  Build Release + tự tăng phiên bản.

  Mẫu phiên bản: vMYY.DDMM.{A|P}N   vd v226.0610.A4
    M    = năm - 2024 (2026 -> 2, 2027 -> 3)
    YY   = 2 số cuối của năm
    DDMM = ngày + tháng
    A/P  = sáng (trước 12h) / chiều
    N    = lần thay đổi thứ N trong ngày (đếm cả ngày, sang ngày mới về 1)

  Cách dùng:
    .\build-release.ps1                     # tăng 1 bậc rồi build vào .\Release
    .\build-release.ps1 -Note "Thêm X"      # kèm ghi chú vào RELEASE_HISTORY.md
    .\build-release.ps1 -NoBump             # build lại, giữ nguyên số phiên bản
#>
param(
    [switch]$NoBump,
    [string]$Note = ""
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$propsPath = Join-Path $root 'version.props'
$releaseDir = Join-Path $root 'Release'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$oldProps = [IO.File]::ReadAllText($propsPath)
if ($oldProps -notmatch '<AppVersionTag>(v(\d)(\d\d)\.(\d{4})\.([AP])(\d+))</AppVersionTag>') {
    throw "Không đọc được AppVersionTag trong version.props"
}
$oldTag = $Matches[1]
$oldDate = $Matches[3] + $Matches[4]   # YY + DDMM
$oldCount = [int]$Matches[6]

if ($NoBump) {
    $tag = $oldTag
} else {
    $now = Get-Date
    $major = $now.Year - 2024
    $yy = $now.ToString('yy')
    $ddmm = $now.ToString('ddMM')
    $ampm = if ($now.Hour -lt 12) { 'A' } else { 'P' }
    $count = if ($oldDate -eq "$yy$ddmm") { $oldCount + 1 } else { 1 }
    $tag = "v$major$yy.$ddmm.$ampm$count"
}

if ($tag -notmatch '^v(\d)(\d\d)\.(\d{4})\.[AP](\d+)$') { throw "Tag sai mẫu: $tag" }
$numeric = '{0}.{1}.{2}.{3}' -f [int]$Matches[1], [int]$Matches[2], [int]$Matches[3], [int]$Matches[4]

$newProps = $oldProps -replace '<AppVersionTag>[^<]*</AppVersionTag>', "<AppVersionTag>$tag</AppVersionTag>" `
                      -replace '<Version>[^<]*</Version>', "<Version>$numeric</Version>"
[IO.File]::WriteAllText($propsPath, $newProps, $utf8)
Write-Host "Phiên bản: $oldTag -> $tag ($numeric)" -ForegroundColor Cyan

$running = Get-Process -Name ElementsEditor, ElementsCli -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($releaseDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    [IO.File]::WriteAllText($propsPath, $oldProps, $utf8)
    throw "ElementsEditor trong Release đang chạy (PID $($running.Id -join ', ')) - hãy đóng lại rồi build."
}

try {
    if (Test-Path $releaseDir) { Get-ChildItem $releaseDir -Force | Remove-Item -Recurse -Force }
    foreach ($proj in 'src\ElementsEditor', 'src\ElementsCli') {
        # own intermediate folder (.build) so a running Debug/bin copy of the app never locks the build
        & dotnet publish (Join-Path $root $proj) -c Release -o $releaseDir --artifacts-path (Join-Path $root '.build') --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish $proj thất bại (exit $LASTEXITCODE)" }
    }
} catch {
    if (-not $NoBump) { [IO.File]::WriteAllText($propsPath, $oldProps, $utf8); Write-Host "Đã trả version.props về $oldTag" -ForegroundColor Yellow }
    throw
}

$stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm')
[IO.File]::WriteAllText((Join-Path $releaseDir 'VERSION.txt'), "$tag`r`nBuild: $stamp`r`n", $utf8)

$hist = Join-Path $root 'RELEASE_HISTORY.md'
if (-not (Test-Path $hist)) {
    [IO.File]::WriteAllText($hist, "# Lịch sử phiên bản`r`n`r`n| Phiên bản | Build | Ghi chú |`r`n|---|---|---|`r`n", $utf8)
}
$line = "| $tag | $stamp | $($Note -replace '\|', '/') |`r`n"
[IO.File]::AppendAllText($hist, $line, $utf8)

Write-Host "Xong: $releaseDir\ElementsEditor.exe  ($tag)" -ForegroundColor Green
