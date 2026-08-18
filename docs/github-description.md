# GitHub Description 文案（v2.4.0）

## 一、GitHub 仓库 About 短描述（放仓库简介栏）

**英文（推荐，通用）**
> BepInEx plugin for Sephiria (Steam 2436940). Press F8 to auto-arrange your inventory: offline scoring model + simulated annealing, supports tablets, position charms, planet clusters, harmony crystals, dedication badges, compass pairing, hourglass-magicbook synergy and more. Works for host and multiplayer clients.

**中文**
> 《赛菲莉娅》(Steam 2436940) 的 BepInEx 插件。按 F8 一键智能整理背包：全离线评分 + 模拟退火搜索，支持石板、位置护符、行星聚簇、和谐之晶、奉献徽章、指北针配对、沙漏-魔法书联动等，主机与联机客户端均可用。

## 二、完整 README.md（中文版）

---

# Sephiria Backpack Organizer / 赛菲莉娅 背包整理插件

《赛菲莉娅》(Sephiria, Steam AppID 2436940) 的 BepInEx 插件。按 **F8** 一键整理背包，把石板覆盖、护符位置条件、行星聚簇、和谐之晶、奉献徽章、指北针配对、发光的沙漏等所有加成机制尽可能同时吃到。

- 版本：v2.4.0
- 运行环境：BepInEx 6（Unity Mono）/ Unity 6000.3.21f1 / Mirror 联机
- 单机、主机、联机客户端均可用

## 功能

- **一键整理**：按 F8 重排背包，完成后提示"整理完毕"
- **全离线评分模型**：在背包副本上完整模拟游戏加成公式，任何摆法都能打分，搜索以分数为目标
- **多轮模拟退火搜索**：每次整理内部跑 4 轮独立搜索（不同随机种子）取全局最优，一次按键直达最佳；34 格满包约 200~300ms
- **智能初始布局**：石板贪心摆位 → 受限护符 → 行锁定 → 行星望远镜 → 和谐之晶 → 奉献徽章 → 沙漏 → 行星聚簇 → 罗盘配对 → 其余护符 → 负担塞负格
- **支持的机制**（全部离线模拟游戏真实公式）：
  - 石板效果网格（+等级 / ×倍率 / 禁用 / 豁免格）与旋转
  - 护符位置条件（顶行 / 底行 / 两侧 / 内侧 / 外侧 / 两侧空 / 两侧有护符 / 八邻域满 / 靠近魔法书）
  - 豁免格优先（冰锁类）
  - 行星望远镜聚簇（周围 8 格行星，可排除乐谱银河等）
  - 和谐之晶：周围 8 格等级和放大伤害
  - 奉献徽章：同一横排的同伴藏品全部强化
  - 指北针配对：上方为伤害类藏品才生效
  - **发光的沙漏：自动放到 CD 最长的魔法书左边**（右边格魔法书 CD 恢复 +30/60/100%）
  - 心之重担等负面藏品强制塞进负等级格
  - 神秘地块 ×2（凑齐 2/5 个触发）
  - 行锁定物品（凯尔萨德尼钥匙：保持所在行不变）
  - 武器相关护符（按当前武器类型匹配）
  - 附魔、优先级系统（传说/羁绊 > 稀有 > 高级 > 普通）
- **联机客户端支持**：客户端没有服务器权限，插件离线算好最优布局后，翻译成交换 / 旋转操作序列，用游戏自带网络接口逐步执行，不清空背包
- **安全设计**：进图等待背包初始化（3 秒）、整理前快照一致性校验、一次应用不重试，防止物品丢失

## 安装

1. 安装 [BepInEx 6](https://github.com/BepInEx/BepInEx)（Unity Mono 版本）
2. 把 `SephiriaBackpackOrganizer.dll` 放进 `BepInEx/plugins/`
3. 启动游戏，日志出现 `Sephiria Backpack Organizer v2.4.0 已加载` 即成功

## 使用

- 游戏中按 **F8** 整理背包
- 全部参数在 `BepInEx/config/com.sephiria.backpack-organizer.cfg`，改完重启游戏生效

## 配置速查

| 分区 | 配置项 | 默认 | 说明 |
| --- | --- | --- | --- |
| General | Hotkey | F8 | 触发整理的快捷键 |
| General | SortMode | Enhanced | Vanilla（游戏内置）/ Enhanced（增强） |
| General | SessionStableDelay | 3 秒 | 进图后等待背包初始化（防丢物品） |
| Enhanced | SearchRounds | 4 | 每次整理的独立搜索轮数 |
| Enhanced | Iterations | 3000 | 模拟退火迭代次数 |
| Enhanced | Temperature | 800 | 初始温度（越大越容易跳出局部最优） |
| Priority | Enable | true | 优先级系统（传说=1 … 普通=4） |
| Synergy | PlanetBonus | 40000 | 行星聚簇每颗行星奖励 |
| Synergy | HarmonyLevelBonus | 2000 | 和谐之晶周围每级护符等级 |
| Synergy | DedicationCompanionBonus | 3000 | 奉献徽章每个同行同伴 |
| Synergy | HourglassBonus | 6000 | 沙漏右边魔法书每 CD 秒 |
| Synergy | CompassBonus | 12000 | 指北针配对奖励 |
| Burden | NegativeCellPenalty | 20000 | 负担未待负格扣分 |
| Mystic | Enable | true | 神秘 ×2 地块 |

## 工作原理（简述）

1. **识别**：把背包物品分类（石板 / 护符 / 负面藏品 / 杂物），识别各机制物品（类型或配置 key）
2. **评分**：离线模拟游戏全部加成公式，对任意布局打分（等级 × 优先级权重、启用/禁用、行星聚簇、和谐之晶、奉献徽章、罗盘配对、沙漏-魔法书、负担惩罚、行锁定约束等）
3. **搜索**：智能初始布局 + 多轮多起点模拟退火（定向移动 + 随机探索），取全局最高分布局
4. **应用**：主机直接整包写回；联机客户端翻译成交换 / 旋转序列逐步执行

## 更新日志

见 [CHANGELOG.md](CHANGELOG.md)（v2.0 全离线评分模型 → v2.3 优先级/行星聚簇/行锁定/多轮搜索/和谐之晶 → v2.3.9 奉献徽章 → v2.4.0 发光的沙漏配对）。

## 免责声明

本插件不修改任何游戏文件，仅供学习与个人使用；使用本插件产生的任何后果由使用者自行承担。

---

## 三、README.md（英文精简版，可选）

# Sephiria Backpack Organizer

A BepInEx plugin for *Sephiria* (Steam AppID 2436940, Unity + Mirror). Press **F8** to auto-arrange your inventory so every synergy mechanic triggers at once.

**v2.4.0 · BepInEx 6 (Unity Mono) · host & multiplayer client**

## Features

- One-key arrangement (F8), finishes with an in-game toast
- Fully offline scoring model: evaluates any layout against the game's real formulas (tablet grids, position conditions, enchant, mystic ×2 …)
- Simulated annealing with multiple starts & rounds (default 4 rounds, different seeds) — one press reaches the best result; ~200–300 ms on a full 34-slot bag
- Smart initial layout: tablets → restricted charms → row-locked items → planet module → harmony crystals → dedication badge → hourglass → planet clustering → compass pairing → the rest → burdens to worst cells
- Supported synergies: planet telescope clusters, harmony crystal level-sum, dedication badge row buff, compass pairing, **glowing hourglass placed left of the longest-CD magic book**, burden dumping, mystic ×2, row-locked items (Kelsardanni Key), weapon-matched charms, priority system
- Multiplayer client support: computes the optimal layout locally, then applies it via the game's own network ops (Swap / DoClickAction) — no server authority needed, never clears the bag
- Safety: 3s session-stable delay, snapshot verification before sorting, apply-once

## Install

1. Install BepInEx 6 (Unity Mono build)
2. Drop `SephiriaBackpackOrganizer.dll` into `BepInEx/plugins/`
3. Launch the game; the log shows `Sephiria Backpack Organizer v2.4.0`

## Usage

Press **F8** in game. All settings live in `BepInEx/config/com.sephiria.backpack-organizer.cfg` (restart to apply).

See [CHANGELOG.md](CHANGELOG.md) for the full history.
