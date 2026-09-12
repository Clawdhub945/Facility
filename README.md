# Facility — 综合生产所

《领地：种田与征战》(Territory) 的 BepInEx 6 / IL2CPP Mod：新增一座 **3×3 可派工的每日产出建筑「综合生产所」**。

## 功能

- 建造菜单「食品」分类新增 **综合生产所**（免科技，耗材 原木×30，3×3 占地）
- 建筑使用游戏原生的工人系统：**可安排人工作，人数可调**（窗口 +/- 可在 1..每座最大工位数 间调整，采集营地同款外壳与外观，贴图全部用游戏自带资源）
- **每个工人每个游戏日稳定产出**，默认 `10 原木 + 10 石料`，产出自动进建筑袋子，由搬运工入库
- 产品清单可自由扩展（见下）

## 配置（BepInEx/config/claude.facility.cfg）

```ini
[生产]
## 格式：物品id:每人每日数量，多组用英文逗号分隔
## 604001=原木，605001=石料
每人每日产出 = 604001:10,605001:10
每座最大工位数 = 10

[调试]
## 默认 false=安静模式；true 输出每座建筑每项产出的明细日志
日志详细模式 = false
```

**加产品**：按 `物品id:每人每日数量` 追加即可，例如 `604001:10,605001:10,612001:1`（每人每日额外 +1 红宝石）。
改完最多 1.5 秒热生效，无需重启。

## 安装

- 创意工坊：订阅后自动启用
- 手动：把 `Facility.dll` 放进 `BepInEx/plugins/Facility/`，`Defs/` 文件夹一并放进同目录

## 机制说明

- 建筑本体是纯数据 Def 注入（`Defs/{stuff,build,tech,career,blueprint}.json`，行拷贝自官方采集营地 105006，
  走游戏官方 Mod Def 通道：`plugins/<mod>/Defs/*.json`）
- **blueprint.json（产品蓝图表）是产品记录/数据键的总开关**：`FacilityHuntingCabin.GetProductDataKeyList(stuff_id)`
  按设施 id 查蓝图字典，缺行会抛 KeyNotFoundException 并炸断整个窗口绑定
  （工人数 99/99、库存空、假产量记录），还会让主任务循环 NpcTaskHelper.Tick 反复报错
- **每座最大工位数**：Harmony 补丁 `Facility.GetOriginalWorkPosCount`（仅 105040），
  窗口 +/- 的调整上限即此值
- 每日产出由 DLL 驱动：检测游戏日历（`Clock.Now`）换日后，按每座建筑当前工人数 × 产出表
  调用 `Facility.AddStuff` 入袋；无工人的建筑当天跳过
- **career.json（职业表）是工人系统的总开关**：`Facility.GetOriginalWorkPosCount` 查
  `D.career_dic_with_facility_id_as_key[stuff_id]`，缺行 = 工位数 0 = 建筑窗口不渲染工人控件、
  永远分不到工人（0.1.0 实测教训）。本 mod 复用采集者职业（npc_type=8）
- tech 行的 `txt_id` 是分类段号（100=初始 200=住所 300=食品 3700=制造 1500=物流 600=路桥），
  用错段号建筑不会出现在对应分类的建造菜单里

## 构建

```
dotnet build -c Release
python _tools/make_defs.py --deploy   # 生成并部署 Defs 到 C:\TerritoryModTest
```

本地测试：DLL 与 Defs 放 `C:\TerritoryModTest`（游戏启动镜像同步进 plugins/1006）。
直接放 plugins 下自建文件夹会被游戏清掉。
