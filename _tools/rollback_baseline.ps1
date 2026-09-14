# 一键回滚到 2026-09-14 基线（实验失败时用）
$bk = "C:\AI\mod\_baseline_20260914"
Copy-Item "$bk\Defs\*" "C:\TerritoryModTest\Defs" -Recurse -Force
Copy-Item "$bk\Textures\*" "C:\TerritoryModTest\Textures" -Recurse -Force
Copy-Item "$bk\dll\Facility.dll" "C:\TerritoryModTest\Facility.dll" -Force
Write-Host "已回滚到基线。重启游戏生效。"