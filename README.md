# Facility — 综合生产所

《领地：种田与征战》(Territory) 的 BepInEx 6 / IL2CPP Mod：新增一座 **3×3 可派工的每日产出建筑「综合生产所」**。

## 功能

- 建造菜单「食品」分类新增 **综合生产所**（免科技，耗材 原木×30，3×3 占地）
- 建筑使用游戏原生的工人系统：**可安排人工作，人数可调**（窗口 +/- 可在 1..每座最大工位数 间调整，采集营地同款外壳与外观，贴图全部用游戏自带资源）
- **每个工人每个游戏日稳定产出**，默认 `10 原木 + 10 石料`，产出自动进建筑袋子，由搬运工入库
- 产出同时计入 **窗口的「今年产量/去年产量」记录区**（走官方 `Facility.RecordProduct`）
- 产品清单可自由扩展（见下）
- 窗口里可点选**额外产品**（铁矿/秘银矿/红宝石……），选择即时写入配置并热生效

## 配置（BepInEx/config/claude.facility.cfg）

```ini
[生产]
## 格式：物品id:每人每日数量，多组用英文逗号分隔
## 604001=原木，605001=石料
每人每日产出 = 604001:10,605001:10
每座最大工位数 = 10
## 窗口选择条的候选产品（物品id:名称）与当前选择（0=无）
额外产品候选 = 616001:铁矿,616002:秘银矿,612001:红宝石
额外产品 = 0
额外产品每日数量 = 10

[调试]
## 默认 false=安静模式；true 输出每座建筑每项产出的明细日志（含选择条屏幕坐标，供自动化点击）
日志详细模式 = false
```

**加产品**：按 `物品id:每人每日数量` 追加即可，例如 `604001:10,605001:10,612001:1`（每人每日额外 +1 红宝石）。
改完最多 1.5 秒热生效，无需重启。


## 窗口 UI 定制

建筑窗口内做了两处定制（Harmony 补丁 `WindowGatherersHut.SetInfo` + 刷新链）：

1. **改写文案**：原版「附近森林覆盖率 xx%，资源稀少」对本建筑无意义，
   改为「综合生产所：工人每日自动产出（数量可在配置调整）」，
   底部提示也改为产出说明（游戏会周期性重写这些文本，补丁每次刷新都会再改回）
2. **额外产品选择条**：文案下方注入一条可点击的原生 uGUI 选择条，
   点击循环切换 `无 → 铁矿 → 秘银矿 → 红宝石 → 无…`，选择直接写入 cfg 并热生效
   （候选清单由 cfg「额外产品候选」配置，格式 物品id:名称）

> 实现注记：最初用 Unity 的 `Dropdown` 构建下拉框——控件能正常显示，
> 但射线探测显示点击命中自身却打不开列表（游戏的自定义输入派发）。改用 `Button`
> 循环选择条后一次通过：点击 → 写 cfg → 下一游戏日产出（实测 10 人 → 铁矿 +100/日）。

## 安装

- 创意工坊：订阅后自动启用
- 手动：把 `Facility.dll` 放进 `BepInEx/plugins/Facility/`，`Defs/` 文件夹一并放进同目录

## 机制说明

- 建筑本体是纯数据 Def 注入（`Defs/{stuff,build,tech,career,blueprint}.json`，行拷贝自官方采集营地 105006，
  走游戏官方 Mod Def 通道：`plugins/<mod>/Defs/*.json`）
- **blueprint.json（产品蓝图表）是产品记录/数据键的总开关**：`FacilityHuntingCabin.GetProductDataKeyList(stuff_id)`
  按设施 id 查蓝图字典，缺行会抛 KeyNotFoundException 并炸断整个窗口绑定
  （工人数 99/99、库存空、假产量记录），还会让主任务循环 NpcTaskHelper.Tick 反复报错。
  0.4.0 起为 原木/石料/铁矿/秘银矿/红宝石 各生成一行（`formula_id` 槽位 40 起）
- **产量记录**：每日产出同时调 `Facility.AddStuff`（入袋）与 `Facility.RecordProduct`（记账），
  后者是窗口「今年/去年产量」记录区的唯一数据源（官方 `Facility.cs:71`）
- **换日判据 = `Clock.Days`**（游戏日计数，存档 `summary.sav` 里的 `days` 字段，官方源码 264 处判日都用它）。
  ⚠ 不要用 `Clock.Now` 的年月日拼日键——0.4.0 前这么写导致一天之内重复产出几百次（日志实证 512 次）
- **每座最大工位数**：Harmony 补丁 `Facility.GetOriginalWorkPosCount`（仅 105040），
  窗口 +/- 的调整上限即此值
- 每日产出由 DLL 驱动：检测游戏日历换日后，按每座建筑当前工人数 × 产出表
  调用 `Facility.AddStuff` + `RecordProduct`；无工人的建筑当天跳过；同一建筑同一天只发一次（防重）
- **career.json（职业表）是工人系统的总开关**：`Facility.GetOriginalWorkPosCount` 查
  `D.career_dic_with_facility_id_as_key[stuff_id]`，缺行 = 工位数 0 = 建筑窗口不渲染工人控件、
  永远分不到工人（0.1.0 实测教训）。本 mod 复用采集者职业（npc_type=8）
- tech 行的 `txt_id` 是分类段号（100=初始 200=住所 300=食品 3700=制造 1500=物流 600=路桥），
  用错段号建筑不会出现在对应分类的建造菜单里

## 自动化测试

```bash
python _tools/e2e.py            # 全自动：启游戏 → 进最新档 → 定位建筑 → 点开窗口 → 点选择条 → 断言 cfg/日志
python _tools/e2e.py --no-load  # 已在存档里时跳过读档
```

11 项断言（mod 加载 / HTTP 就绪 / 进档 / 建筑存在 / 开窗 / 选择条坐标 /
真鼠标点击改 cfg / F8 钩子改 cfg / 换日产出 / 同日不重复产出 / 数量=工人数×每人数量），
逐项 PASS/FAIL、退出码 0/1。底层能力在 `_tools/autotest.py`（截图、鼠标键盘、
进程与读档、增量读日志、HTTP 只读端点），纯标准库 + Pillow，不需要 pyautogui/pywin32。

> 细节与踩坑（SendInput 绝对坐标被吞、BepInEx 控制台窗口标题含 "Territory" 会误选、
> 游戏内帝国地图弹窗会挡住点击……）见 [docs/开发交接.md](docs/开发交接.md) §10。

## 继续开发

接手开发的请看 **[docs/开发交接.md](docs/开发交接.md)**：里面记录了
游戏 Mod Def 五张表（stuff/build/tech/career/blueprint）各自缺失时的症状与坑、
关键游戏 API、调试方法论（Player.log 找异常 / 侦察补丁 / 射线探测）、
以及本项目的踩坑清单。

## 构建

```
MSBuildEnableWorkloadResolver=false dotnet build -c Release   # 必须带这个环境变量（见交接文档 §2.1）
python _tools/make_defs.py --deploy   # 生成并部署 Defs 到 C:\TerritoryModTest
python _tools/e2e.py                  # 全自动回归测试
```

本地测试：DLL 与 Defs 放 `C:\TerritoryModTest`（游戏启动镜像同步进 plugins/1006）。
直接放 plugins 下自建文件夹会被游戏清掉。
