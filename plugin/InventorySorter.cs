using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

namespace SephiriaBackpackOrganizer
{
    public enum SortMode
    {
        Vanilla,
        Enhanced
    }

    /// <summary>
    /// 背包整理引擎 v2.0 —— 全离线智能模型。
    ///
    /// v2.0 相对 v1.x 的关键升级：
    /// 1. 【性能】评估从"每次跑游戏权限周期"改为纯离线模型：预计算石板效果模板、
    ///    读取真实 levelMatrix 反推基础等级、附魔等级逐物品计入；单次评估微秒级，
    ///    后期满背包也不再卡顿（原版十几秒 -> 亚秒）。
    /// 2. 【行星望远镜】Charm_PlanetModule 启用时，周围八格 PLANET 分类藏品获得加成
    ///    —— 评分加入"行星聚簇"奖励，搜索会把行星藏品摆到望远镜周围。
    /// 3. 【指北针】Charm_UpCharmDamage 置于伤害类藏品(IAttackableCharm)下方，
    ///    或指北针下方再放指北针可链式叠加 —— 评分加入"罗盘配对"奖励。
    /// 4. 【附魔】物品自身附魔等级(Enchant)随物品走，计入格位等级。
    /// 5. 【石板位置条件】旗帜(最左列)/遮阳(最上行)等通过 conditionQuery 边界 token
    ///    表达，智能摆位按游戏解析器语义评估条件，不满足条件的效果不计入。
    /// 6. 【心之重担等负面藏品】按 LocalizedString key 识别，直接塞进负数加成格子。
    /// 7. 【稀有度优先】高稀有度藏品优先获得等级加成，必要时低级藏品被牺牲进负格。
    /// 8. 安全兜底：最终结果按离线评分与原始布局比较，绝不更差。
    /// </summary>
    public class InventorySorter
    {
        private readonly Plugin plugin;
        private bool busy;
        private float sessionStartTime = -1f;

        public bool Busy => busy;

        /// <summary>会话断开时重置初始化计时（下次进入新会话重新等待背包就绪）。</summary>
        public void ResetSessionClock()
        {
            sessionStartTime = -1f;
        }

        public InventorySorter(Plugin plugin)
        {
            this.plugin = plugin;
        }

        // ---------------------------------------------------------------- 单格状态

        private sealed class Slot
        {
            public bool hasItem;
            public int instanceID;
            public int entityID;
            public sbyte quantity;
            public Charm_Basic charm;
            public StoneTablet tablet;
            public int rotation;

            public Slot Clone()
            {
                return new Slot
                {
                    hasItem = hasItem,
                    instanceID = instanceID,
                    entityID = entityID,
                    quantity = quantity,
                    charm = charm,
                    tablet = tablet,
                    rotation = rotation
                };
            }

            public static Slot Empty() => new Slot();
        }

        // ---------------------------------------------------------------- 护符位置条件

        private enum CharmPositionKind
        {
            None,
            Top,
            Bottom,
            Side,
            Inside,
            Outlined,
            BothSidesEmpty,
            BothSideCharm,
            NeighborsFull,
            NearMagicBook,
            FullHp
        }

        private static CharmPositionKind GetPositionKind(Charm_Basic charm)
        {
            if (charm == null || charm.criteria == null)
            {
                return CharmPositionKind.None;
            }

            if (charm.criteria is CharmActivateCriteria_TopInInventory) return CharmPositionKind.Top;
            if (charm.criteria is CharmActivateCriteria_BottomInInventory) return CharmPositionKind.Bottom;
            if (charm.criteria is CharmActivateCriteria_SideEnd) return CharmPositionKind.Side;
            if (charm.criteria is CharmActivateCriteria_Inside) return CharmPositionKind.Inside;
            if (charm.criteria is CharmActivateCriteria_Outlined) return CharmPositionKind.Outlined;
            if (charm.criteria is CharmActivateCriteria_BothSidesAreEmpty) return CharmPositionKind.BothSidesEmpty;
            if (charm.criteria is CharmActivateCriteria_BothSideCharm) return CharmPositionKind.BothSideCharm;
            if (charm.criteria is CharmActivateCriteria_NeighborsAreFull) return CharmPositionKind.NeighborsFull;
            if (charm.criteria is CharmActivateCriteria_Near8MagicBook) return CharmPositionKind.NearMagicBook;
            return CharmPositionKind.None;
        }

        private static bool IsSatisfyingCell(GridInventory inv, CharmPositionKind kind, int x, int y, int index, int storage, int width)
        {
            switch (kind)
            {
                case CharmPositionKind.Top:
                    return y == 0;
                case CharmPositionKind.Bottom:
                    return index >= storage - 6;
                case CharmPositionKind.Side:
                    return x == 0 || x >= width - 1;
                case CharmPositionKind.Inside:
                    return x > 0 && y > 0 && x < width - 1 && index + 7 <= storage - 1;
                case CharmPositionKind.Outlined:
                    return x <= 0 || y <= 0 || x >= width - 1 || index >= storage - 6;
                default:
                    return true;
            }
        }

        private static int KindPriority(CharmPositionKind kind)
        {
            switch (kind)
            {
                case CharmPositionKind.Top:
                case CharmPositionKind.Bottom:
                case CharmPositionKind.Side:
                    return 3;
                case CharmPositionKind.Inside:
                case CharmPositionKind.Outlined:
                    return 2;
                default:
                    return 1;
            }
        }

        private static readonly ItemPosition[] Neighbor8 =
        {
            new ItemPosition(-1, 0), new ItemPosition(1, 0),
            new ItemPosition(0, -1), new ItemPosition(0, 1),
            new ItemPosition(-1, -1), new ItemPosition(1, -1),
            new ItemPosition(-1, 1), new ItemPosition(1, 1)
        };

        // ---------------------------------------------------------------- 物品信息与石板效果模板

        private sealed class ItemInfo
        {
            public int index;
            public Slot slot;
            public bool isStele;
            public bool isCharm;
            public bool isBurden;
            public bool isPlanetCategory;   // Entity.categories.Contains("PLANET")
            public bool excludeFromPlanetCluster; // 行星分类但不参与望远镜聚簇（如乐谱银河）
            public bool isPlanetModule;     // Charm_PlanetModule（行星望远镜）
            public bool isHarmonyCrystal;   // Charm_NearLevelDamage（和谐之晶：周围8格护符等级和→伤害放大）
            public bool isDedicationBadge;  // Charm_CompanionChaos（奉献徽章：加成同一横排的同伴）
            public bool isDedicationCompanion; // ICompanionCharm（同伴：金色手铃/迷你弩炮/灵魂粉末等）
            public bool isHourglass;         // Charm_RightSpellCooldownHelper（发光的沙漏：右边格魔法书 CD 变短）
            public bool isMagicBook;         // Charm_Magic（魔法书：ContainedMagic 提供 CD 与伤害）
            public float magicCd;            // 魔法书 CD 秒数（ActiveSkillEntity.cooldownTime）
            public bool isCompass;          // Charm_UpCharmDamage（指北针）
            public bool isAttackable;       // IAttackableCharm
            public CharmPositionKind kind;
            public EItemRarity rarity;
            public int priority = 4;   // 用户优先级：1最高~4最低（传说/羁绊=1 稀有=2 高级=3 普通=4，特定藏品强制1）
            public bool preferIgnoreCells; // 优先利用豁免格解除位置限制（如冰冷的锁）
            public bool isRowLocked;   // 行锁定：物品类型随所在行变化（如凯尔萨德尼钥匙），整理不可变行
            public int lockRow;        // 行锁定物品的固定行（用户摆放时的行）
            public int enchant;
            public int maxLevel;
            public ItemEntity entity;
        }

        private struct EffectEntry
        {
            public int cell;      // 格子索引
            public byte kind;     // 0=加等级 1=禁用 2=豁免 3=乘等级
            public int value;     // 等级增量/乘数
        }

        private struct ConditionEntry
        {
            public int cell;                          // 格子索引
            public StoneTablet.CriteriaType type;     // 条件类型
        }

        /// <summary>一块石板在某个(格子,旋转)下的效果模板与条件（预计算）。</summary>
        private sealed class StelePattern
        {
            public int cell;
            public int rotation;
            public List<EffectEntry> effects = new List<EffectEntry>();
            public List<ConditionEntry> conditions = new List<ConditionEntry>();
            public bool hasPlacedCondition;
        }

        /// <summary>一次整理的全部预计算上下文。</summary>
        private sealed class SearchContext
        {
            public GridInventory inv;
            public int storage;
            public int width;
            public int height;
            public int[] baseLevel;               // 无石板贡献、无附魔的基础格位等级
            public List<Slot> original;           // 原始布局
            public List<ItemInfo> items = new List<ItemInfo>();
            public List<ItemInfo> steles = new List<ItemInfo>();
            public List<ItemInfo> charms = new List<ItemInfo>();
            public List<ItemInfo> burdens = new List<ItemInfo>();
            public List<ItemInfo> others = new List<ItemInfo>();
            public Dictionary<int, Dictionary<int, StelePattern>> stelePatterns; // tabletInstanceID -> cell*4+rot -> pattern
            public Dictionary<int, ItemInfo> itemByInstance = new Dictionary<int, ItemInfo>(); // instanceID -> ItemInfo
            public int[] mysticFactor = new int[0]; // 神秘地块等级倍率（默认 1；神秘藏品≥2时1格×2，≥5时4格×2）
            public int mysticCount;                  // 神秘分类藏品数量（游戏组合计数）
            public int mysticActiveCells;            // 实际生效的 ×2 地块数
            public int[] cellLevel = new int[0];  // 评估时复用缓冲
            public bool[] disabled = new bool[0];
            public bool[] ignore = new bool[0];
        }

        // ---------------------------------------------------------------- 石板查询解析

        private static List<StoneTablet.AdditionMetadata> ParseQuerySafe(
            StoneTablet tablet, ItemPosition origin, int rotation, int width, int height, int storage, bool condition)
        {
            try
            {
                string q = condition
                    ? tablet.GetConditionQuery(tablet.instanceID)
                    : tablet.GetQuery(tablet.instanceID);
                if (string.IsNullOrEmpty(q))
                {
                    return new List<StoneTablet.AdditionMetadata>();
                }
                return StoneTablet.ParseQuery(q, width, height, storage, origin, rotation, out _);
            }
            catch
            {
                return new List<StoneTablet.AdditionMetadata>();
            }
        }

        private static bool InBounds(int x, int y, int width, int height)
        {
            return x >= 0 && y >= 0 && x < width && y < height;
        }

        /// <summary>安全格子访问：越界（含 storage 尾部不完整行）返回 null。</summary>
        private static Slot At(List<Slot> slots, int x, int y, int width, int storage)
        {
            if (x < 0 || y < 0 || x >= width)
            {
                return null;
            }
            int idx = y * width + x;
            if (idx < 0 || idx >= storage || idx >= slots.Count)
            {
                return null;
            }
            return slots[idx];
        }

        // ---------------------------------------------------------------- 上下文构建

        private SearchContext BuildContext(GridInventory inv, List<Slot> original)
        {
            int storage = inv.CurrentInventoryStorage;
            int w = inv.Width;
            int h = inv.GetHeight(storage);

            var ctx = new SearchContext
            {
                inv = inv,
                storage = storage,
                width = w,
                height = h,
                original = original,
                baseLevel = new int[storage],
                cellLevel = new int[storage],
                disabled = new bool[storage],
                ignore = new bool[storage],
                mysticFactor = new int[storage]
            };

            // 分类物品
            for (int i = 0; i < original.Count && i < storage; i++)
            {
                Slot s = original[i];
                if (s == null || !s.hasItem)
                {
                    continue;
                }

                var info = new ItemInfo { index = i, slot = s, isStele = s.tablet != null, isCharm = s.charm != null };
                if (info.isCharm)
                {
                    info.kind = GetPositionKind(s.charm);
                    info.maxLevel = s.charm.maxLevel;
                    info.isPlanetModule = s.charm is Charm_PlanetModule;
                    info.isHarmonyCrystal = s.charm is Charm_NearLevelDamage;
                    info.isDedicationBadge = s.charm is Charm_CompanionChaos;
                    info.isDedicationCompanion = s.charm is ICompanionCharm;
                    info.isHourglass = s.charm is Charm_RightSpellCooldownHelper;
                    info.isMagicBook = s.charm is Charm_Magic;
                    if (info.isMagicBook && s.charm is Charm_Magic mg && mg.ContainedMagic != null)
                    {
                        info.magicCd = mg.ContainedMagic.cooldownTime;
                    }
                    info.isCompass = s.charm is Charm_UpCharmDamage;
                    info.isAttackable = s.charm is IAttackableCharm ac && ac.IsAttackableCharm();
                }

                try
                {
                    info.entity = ItemDatabase.FindItemById(s.entityID);
                    if (info.entity != null)
                    {
                        info.rarity = info.entity.rarity;
                        if (info.entity.categories != null)
                        {
                            info.isPlanetCategory = info.entity.categories.Contains("PLANET");
                            // 行星分类但不参与望远镜聚簇的藏品（如乐谱银河）
                            if (info.isPlanetCategory && info.entity.aName != null &&
                                !string.IsNullOrEmpty(info.entity.aName.key))
                            {
                                foreach (string key in plugin.PlanetClusterExcludedItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.excludeFromPlanetCluster = true;
                                        break;
                                    }
                                }
                            }
                        }
                        // 负面藏品识别（心之重担等，按 LocalizedString key）
                        if (info.entity.aName != null && !string.IsNullOrEmpty(info.entity.aName.key))
                        {
                            foreach (string key in plugin.BurdenItemKeys.Value.Split(new[] { ',', ';' },
                                         StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (info.entity.aName.key.Trim() == key.Trim())
                                {
                                    info.isBurden = true;
                                    break;
                                }
                            }

                            // 用户优先级：稀有度映射 + 强制最高优先级物品
                            if (plugin.PriorityEnable.Value)
                            {
                                info.priority = RarityToPriority(info.rarity);
                                foreach (string key in plugin.PriorityFixedItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.priority = 1;
                                        break;
                                    }
                                }
                                // 优先豁免格物品（如冰冷的锁：有解锁石板就尽可能利用）
                                foreach (string key in plugin.IgnoreCellPreferredItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.preferIgnoreCells = true;
                                        break;
                                    }
                                }
                                // 行锁定物品（如凯尔萨德尼钥匙：类型随所在行变化，整理不可变行）
                                foreach (string key in plugin.RowLockedItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isRowLocked = true;
                                        info.lockRow = i / w; // 记录用户摆放时的行（i 为原始格子索引）
                                        break;
                                    }
                                }
                                // 和谐之晶类（可扩展；类识别已覆盖默认物品）
                                foreach (string key in plugin.HarmonyCrystalItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isHarmonyCrystal = true;
                                        break;
                                    }
                                }
                                // 奉献徽章类（可扩展；类识别已覆盖默认物品）
                                foreach (string key in plugin.DedicationBadgeItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isDedicationBadge = true;
                                        break;
                                    }
                                }
                                // 发光的沙漏类（可扩展；类识别已覆盖默认物品）
                                foreach (string key in plugin.HourglassItems.Value.Split(new[] { ',', ';' },
                                             StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (info.entity.aName.key.Trim() == key.Trim())
                                    {
                                        info.isHourglass = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 忽略数据异常
                }

                // 附魔等级（物品自身携带，随物品走）
                try
                {
                    string enchantStr = DungeonManager.Instance != null
                        ? DungeonManager.Instance.GetGlobalItemStatValue(s.instanceID, "Enchant")
                        : "";
                    if (int.TryParse(enchantStr, out int e) && e != 0)
                    {
                        info.enchant = e;
                    }
                }
                catch
                {
                }

                ctx.items.Add(info);
                ctx.itemByInstance[s.instanceID] = info;
                if (info.isBurden) ctx.burdens.Add(info);
                else if (info.isStele) ctx.steles.Add(info);
                else if (info.isCharm) ctx.charms.Add(info);
                else ctx.others.Add(info);
            }

            // 预计算每块石板的全部摆放模板（以石板实例ID为键）
            ctx.stelePatterns = new Dictionary<int, Dictionary<int, StelePattern>>();
            foreach (ItemInfo stele in ctx.steles)
            {
                bool rotatable = DungeonManager.IsTabletRotatable(stele.slot.tablet.instanceID, stele.slot.tablet.isRotatable);
                int rotations = rotatable ? 4 : 1;
                var byCell = new Dictionary<int, StelePattern>();
                for (int cell = 0; cell < storage; cell++)
                {
                    ItemPosition origin = inv.IdxToPos(cell);
                    for (int rot = 0; rot < rotations; rot++)
                    {
                        byCell[cell * 4 + rot] = BuildStelePattern(ctx, stele.slot.tablet, origin, rot);
                    }
                }
                ctx.stelePatterns[stele.slot.tablet.instanceID] = byCell;
            }

            // 神秘地块：神秘分类藏品≥2时 1 格 ×2，≥5时 4 格 ×2（ComboEffect_Mystic 在 mysticPositions 上放 ×2 固定附魔）
            for (int i = 0; i < storage; i++)
            {
                ctx.mysticFactor[i] = 1;
            }
            if (plugin.MysticEnable.Value)
            {
                ctx.mysticCount = 0;
                try
                {
                    // 权威来源：游戏组合计数（键与 categories 标签一致）
                    foreach (var kv in inv.currentSetEffectCount)
                    {
                        if (string.Equals(kv.Key, plugin.MysticCategory.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.mysticCount = kv.Value;
                            break;
                        }
                    }
                }
                catch
                {
                }
                if (ctx.mysticCount <= 0)
                {
                    // 兜底：直接统计护符标签（大小写不敏感）
                    foreach (ItemInfo info in ctx.items)
                    {
                        if (info.isCharm && info.entity != null && info.entity.categories != null &&
                            info.entity.categories.Any(c =>
                                string.Equals(c, plugin.MysticCategory.Value, StringComparison.OrdinalIgnoreCase)))
                        {
                            ctx.mysticCount++;
                        }
                    }
                }
                int active = ctx.mysticCount >= 5 ? 4 : (ctx.mysticCount >= 2 ? 1 : 0);
                ctx.mysticActiveCells = 0;
                if (active > 0 && inv.mysticPositions != null && inv.mysticPositions.Count > 0)
                {
                    int factor = Math.Max(1, (int)plugin.MysticMultiplier.Value);
                    int limit = Math.Min(active, inv.mysticPositions.Count);
                    for (int k = 0; k < limit; k++)
                    {
                        ItemPosition mp = inv.mysticPositions[k];
                        int cell = inv.PosToIdx(mp);
                        if (cell >= 0 && cell < storage)
                        {
                            ctx.mysticFactor[cell] = factor;
                            ctx.mysticActiveCells++;
                        }
                    }
                }
            }

            // 基础等级 = 真实 levelMatrix - 当前石板贡献 - 当前物品附魔（神秘×2地块先除倍率再减）
            int[] currentStele = ComputeSteleContribution(ctx, original);
            for (int i = 0; i < storage; i++)
            {
                int real = 0;
                if (inv.levelMatrix.TryGetValue(inv.IdxToPos(i), out int v))
                {
                    real = v;
                }
                int enchant = 0;
                if (original[i] != null && original[i].hasItem)
                {
                    foreach (ItemInfo info in ctx.items)
                    {
                        if (info.index == i)
                        {
                            enchant = info.enchant;
                            break;
                        }
                    }
                }
                int factor = ctx.mysticFactor[i];
                ctx.baseLevel[i] = (factor > 1 ? real / factor : real) - currentStele[i] - enchant;
            }

            return ctx;
        }

        private static StelePattern BuildStelePattern(SearchContext ctx, StoneTablet tablet, ItemPosition origin, int rotation)
        {
            var pattern = new StelePattern
            {
                cell = ctx.inv.PosToIdx(new ItemPosition(origin.x, origin.y)),
                rotation = rotation
            };

            var metas = ParseQuerySafe(tablet, origin, rotation, ctx.width, ctx.height, ctx.storage, condition: false);
            foreach (var meta in metas)
            {
                var eff = new StoneTablet.AdditionEffectData(meta);
                if (eff.position.x < 0 || eff.position.y < 0 ||
                    eff.position.x >= ctx.width || eff.position.y >= ctx.height)
                {
                    continue; // 越界格：无害
                }
                int cell = ctx.inv.PosToIdx(new ItemPosition(eff.position.x, eff.position.y));
                switch (eff.effectType)
                {
                    case StoneTablet.EffectType.IncreaseConstLevel:
                        pattern.effects.Add(new EffectEntry { cell = cell, kind = 0, value = eff.levelParam });
                        break;
                    case StoneTablet.EffectType.Disable:
                        pattern.effects.Add(new EffectEntry { cell = cell, kind = 1 });
                        break;
                    case StoneTablet.EffectType.IgnoreCriteria:
                        pattern.effects.Add(new EffectEntry { cell = cell, kind = 2 });
                        break;
                    case StoneTablet.EffectType.MultiplyConstLevel:
                        pattern.effects.Add(new EffectEntry { cell = cell, kind = 3, value = eff.levelParam });
                        break;
                }
            }

            var condMetas = ParseQuerySafe(tablet, origin, rotation, ctx.width, ctx.height, ctx.storage, condition: true);
            foreach (var meta in condMetas)
            {
                var crit = new StoneTablet.AdditionCriteriaData(meta);
                if (crit.effectType == StoneTablet.CriteriaType.None)
                {
                    continue;
                }
                if (crit.position.x < 0 || crit.position.y < 0 ||
                    crit.position.x >= ctx.width || crit.position.y >= ctx.height)
                {
                    continue;
                }
                int cell = ctx.inv.PosToIdx(new ItemPosition(crit.position.x, crit.position.y));
                pattern.conditions.Add(new ConditionEntry { cell = cell, type = crit.effectType });
                if (crit.effectType == StoneTablet.CriteriaType.Placed)
                {
                    pattern.hasPlacedCondition = true;
                }
            }

            return pattern;
        }

        /// <summary>石板条件是否满足（镜像游戏 ApplyEffect 判定）。</summary>
        private static bool ConditionsOk(StelePattern pattern, List<Slot> slots)
        {
            if (pattern.conditions.Count == 0)
            {
                return true;
            }

            bool allOk = true;
            bool hasPlaced = false;
            bool placedHit = false;

            foreach (ConditionEntry c in pattern.conditions)
            {
                Slot s = c.cell >= 0 && c.cell < slots.Count ? slots[c.cell] : null;
                bool ok;
                switch (c.type)
                {
                    case StoneTablet.CriteriaType.AnyItem:
                        ok = s != null && s.hasItem;
                        break;
                    case StoneTablet.CriteriaType.OnlyCharm:
                        ok = s != null && s.hasItem && s.charm != null;
                        break;
                    case StoneTablet.CriteriaType.Placed:
                        ok = true;
                        hasPlaced = true;
                        placedHit |= c.cell == pattern.cell;
                        break;
                    default:
                        ok = true;
                        break;
                }
                allOk &= ok;
            }

            if (!allOk)
            {
                return false;
            }
            if (hasPlaced && !placedHit)
            {
                return false;
            }
            return true;
        }

        private static void ApplyPatternEffects(SearchContext ctx, List<Slot> slots,
            StelePattern pattern, int[] level, bool[] disabled, bool[] ignore)
        {
            if (!ConditionsOk(pattern, slots))
            {
                return;
            }
            // 先应用加等级/禁用/豁免，再统一应用乘法（镜像游戏：全部 adds 后 multiply）
            foreach (EffectEntry e in pattern.effects)
            {
                if (e.cell < 0 || e.cell >= ctx.storage)
                {
                    continue;
                }
                switch (e.kind)
                {
                    case 0:
                        level[e.cell] += e.value;
                        break;
                    case 1:
                        if (disabled != null) disabled[e.cell] = true;
                        break;
                    case 2:
                        if (ignore != null) ignore[e.cell] = true;
                        break;
                }
            }
            foreach (EffectEntry e in pattern.effects)
            {
                if (e.kind == 3 && e.cell >= 0 && e.cell < ctx.storage)
                {
                    level[e.cell] *= e.value;
                }
            }
        }

        /// <summary>独立计算某布局下石板对格位的总贡献（adds 求和后再乘），用于反推基础等级。</summary>
        private static int[] ComputeSteleContribution(SearchContext ctx, List<Slot> slots)
        {
            int[] level = new int[ctx.storage];
            int[] mul = new int[ctx.storage];
            for (int i = 0; i < ctx.storage; i++)
            {
                mul[i] = 1;
            }

            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot s = slots[cell];
                if (s == null || !s.hasItem || s.tablet == null)
                {
                    continue;
                }
                if (!ctx.stelePatterns.TryGetValue(s.tablet.instanceID, out var byCell) ||
                    !byCell.TryGetValue(cell * 4 + s.rotation, out var pattern) ||
                    !ConditionsOk(pattern, slots))
                {
                    continue;
                }
                foreach (EffectEntry e in pattern.effects)
                {
                    if (e.cell < 0 || e.cell >= ctx.storage)
                    {
                        continue;
                    }
                    if (e.kind == 0)
                    {
                        level[e.cell] += e.value;
                    }
                    else if (e.kind == 3)
                    {
                        mul[e.cell] *= e.value;
                    }
                }
            }

            for (int i = 0; i < ctx.storage; i++)
            {
                level[i] *= mul[i];
            }
            return level;
        }

        // ---------------------------------------------------------------- 离线评分

        private static bool HasItemAt(List<Slot> slots, int x, int y, int width, int height)
        {
            if (!InBounds(x, y, width, height))
            {
                return false;
            }
            int idx = y * width + x;
            return idx >= 0 && idx < slots.Count && slots[idx] != null && slots[idx].hasItem;
        }

        private static bool HasCharmAt(List<Slot> slots, int x, int y, int width, int height)
        {
            if (!InBounds(x, y, width, height))
            {
                return false;
            }
            int idx = y * width + x;
            return idx >= 0 && idx < slots.Count && slots[idx] != null && slots[idx].hasItem && slots[idx].charm != null;
        }

        /// <summary>护符条件是否满足（含布局依赖型，基于候选布局与当前格子判定；ignored=豁免格）。</summary>
        private static bool CriteriaSatisfied(SearchContext ctx, ItemInfo info, List<Slot> slots, bool ignored, int cell)
        {
            if (ignored)
            {
                return true;
            }

            int x = cell % ctx.width;
            int y = cell / ctx.width;

            switch (info.kind)
            {
                case CharmPositionKind.Top:
                    return y == 0;
                case CharmPositionKind.Bottom:
                    return cell >= ctx.storage - 6;
                case CharmPositionKind.Side:
                    return x == 0 || x >= ctx.width - 1;
                case CharmPositionKind.Inside:
                    return x > 0 && y > 0 && x < ctx.width - 1 && cell + 7 <= ctx.storage - 1;
                case CharmPositionKind.Outlined:
                    return x <= 0 || y <= 0 || x >= ctx.width - 1 || cell >= ctx.storage - 6;

                case CharmPositionKind.BothSidesEmpty:
                    return x > 0 && x < ctx.width - 1 &&
                           !HasItemAt(slots, x - 1, y, ctx.width, ctx.height) &&
                           !HasItemAt(slots, x + 1, y, ctx.width, ctx.height);

                case CharmPositionKind.BothSideCharm:
                    return x > 0 && x < ctx.width - 1 &&
                           HasCharmAt(slots, x - 1, y, ctx.width, ctx.height) &&
                           HasCharmAt(slots, x + 1, y, ctx.width, ctx.height);

                case CharmPositionKind.NeighborsFull:
                    for (int i = 0; i < 8; i++)
                    {
                        if (!HasItemAt(slots, x + Neighbor8[i].x, y + Neighbor8[i].y, ctx.width, ctx.height))
                        {
                            return false;
                        }
                    }
                    return true;

                case CharmPositionKind.NearMagicBook:
                    for (int i = 0; i < 8; i++)
                    {
                        if (HasCharmAt(slots, x + Neighbor8[i].x, y + Neighbor8[i].y, ctx.width, ctx.height) &&
                            slots[y * ctx.width + (x + Neighbor8[i].x)].charm is Charm_Magic)
                        {
                            return true;
                        }
                    }
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>离线评估布局，返回综合评分（越大越好）。
        /// 结构：游戏评分公式（等级/启用/禁用/负格/溢出）+ 稀有度微调 + 行星聚簇 + 罗盘配对 + 负担强制负格。
        /// 注意：完全按"格子"遍历，物品位置以当前布局为准（勿用 ItemInfo.index，那只是原始位置）。</summary>
        private double EvaluateLayout(SearchContext ctx, List<Slot> slots)
        {
            int storage = Math.Min(ctx.storage, slots != null ? slots.Count : 0);
            int[] level = ctx.cellLevel;
            bool[] disabled = ctx.disabled;
            bool[] ignore = ctx.ignore;

            Array.Copy(ctx.baseLevel, level, storage);
            Array.Clear(disabled, 0, storage);
            Array.Clear(ignore, 0, storage);

            // 石板效果（按当前布局中的石板位置/旋转）
            for (int cell = 0; cell < storage; cell++)
            {
                Slot s = slots[cell];
                if (s == null || !s.hasItem || s.tablet == null)
                {
                    continue;
                }
                if (ctx.stelePatterns.TryGetValue(s.tablet.instanceID, out var byCell) &&
                    byCell.TryGetValue(cell * 4 + s.rotation, out var pattern))
                {
                    ApplyPatternEffects(ctx, slots, pattern, level, disabled, ignore);
                }
            }

            double score = 0;

            // 负担惩罚基准：格位最低等级（负担应待在最低/负等级格；无负格时放最低格不罚）
            int minLevel = int.MaxValue;
            bool hasBurden = ctx.burdens.Count > 0 && plugin.BurdenPenalty.Value > 0f;
            if (hasBurden)
            {
                for (int i = 0; i < storage; i++)
                {
                    if (level[i] < minLevel)
                    {
                        minLevel = level[i];
                    }
                }
            }

            // 罗盘配对状态：上方(y-1)为伤害类/指北针的罗盘视为已配对（配对时效果才生效）
            bool[] compassPaired = null;
            if (plugin.CompassBonus.Value > 0f)
            {
                compassPaired = new bool[storage];
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot cs = slots[cell];
                    if (cs == null || !cs.hasItem || cs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(cs.instanceID, out ItemInfo compass) || compass == null || !compass.isCompass)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    Slot above = At(slots, x, y - 1, ctx.width, ctx.storage);
                    if (above != null && above.hasItem && above.charm != null)
                    {
                        bool valid = above.charm is Charm_UpCharmDamage ||
                                     (above.charm is IAttackableCharm ac && ac.IsAttackableCharm());
                        compassPaired[cell] = valid;
                    }
                }
            }

            // 护符/负担评分（按当前布局）
            for (int cell = 0; cell < storage; cell++)
            {
                Slot s = slots[cell];
                if (s == null || !s.hasItem || s.charm == null)
                {
                    continue;
                }
                if (!ctx.itemByInstance.TryGetValue(s.instanceID, out ItemInfo info) || info == null)
                {
                    continue;
                }

                if (info.isBurden)
                {
                    // 负担：不计护符分；所在格越高（离最低格越远）罚越重
                    if (hasBurden)
                    {
                        double excess = level[cell] - minLevel;
                        if (excess > 0)
                        {
                            score -= plugin.BurdenPenalty.Value * excess;
                        }
                    }
                    continue;
                }

                int lvl = (level[cell] + info.enchant) * ctx.mysticFactor[cell];
                // 武器相关护符：游戏内"武器匹配才启用"（flag3 = !isWeaponRelatedCharm || 当前武器类型匹配）
                bool weaponOk;
                if (!info.slot.charm.isWeaponRelatedCharm)
                {
                    weaponOk = true;
                }
                else
                {
                    var wc = info.slot.charm.WeaponController;
                    weaponOk = wc != null && wc.currentWeapon != null &&
                               wc.currentWeapon.weaponType == info.slot.charm.relatedWeapon;
                }
                bool enabled = !disabled[cell] && lvl >= 0 &&
                               CriteriaSatisfied(ctx, info, slots, ignore[cell], cell) &&
                               weaponOk;

                // 行锁定：物品类型随所在行变化（如凯尔萨德尼钥匙），跨行重罚（保持用户选择的类型行）
                if (info.isRowLocked && cell / ctx.width != info.lockRow)
                {
                    score -= 100000;
                }

                // 受限护符位置偏好（引导搜索方向）：
                // 冰锁类（preferIgnoreCells）站豁免格 +5000；普通受限站自然满足位置 +500
                if (KindPriority(info.kind) >= 2)
                {
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    bool natural = IsSatisfyingCell(ctx.inv, info.kind, x, y, cell, ctx.storage, ctx.width);
                    if (info.preferIgnoreCells)
                    {
                        if (ignore[cell])
                        {
                            score += 5000;
                        }
                        else if (natural)
                        {
                            score += 500;
                        }
                    }
                    else if (natural && !ignore[cell])
                    {
                        score += 500;
                    }
                }

                if (enabled)
                {
                    int eff = Mathf.Clamp(lvl, 0, info.maxLevel);
                    // 指北针：效果只在配对时生效，未配对时等级分大幅打折
                    double levelScore = eff * 10000 * PriorityWeight(info.priority);
                    if (info.isCompass && compassPaired != null && !compassPaired[cell])
                    {
                        levelScore *= plugin.CompassUnpairedFactor.Value;
                    }
                    score += levelScore + 1000;
                    if (lvl > info.maxLevel)
                    {
                        score += lvl - info.maxLevel; // 溢出小奖励（镜像游戏）
                    }
                }
                else
                {
                    score -= 750;
                }

                if (lvl < 0)
                {
                    score -= 250 * -lvl; // 负等级暴露惩罚
                }
            }

            // 行星望远镜：启用时周围八格 PLANET 藏品加成
            if (plugin.PlanetBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot ms = slots[cell];
                    if (ms == null || !ms.hasItem || ms.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(ms.instanceID, out ItemInfo module) || module == null || !module.isPlanetModule)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    bool moduleEnabled = !disabled[cell] &&
                                         ((level[cell] + module.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                         CriteriaSatisfied(ctx, module, slots, ignore[cell], cell);
                    if (!moduleEnabled)
                    {
                        continue;
                    }
                    int adjacentPlanets = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + Neighbor8[i].x;
                        int ny = y + Neighbor8[i].y;
                        Slot neighbor = At(slots, nx, ny, ctx.width, ctx.storage);
                        if (neighbor != null && neighbor.hasItem &&
                            ctx.itemByInstance.TryGetValue(neighbor.instanceID, out ItemInfo it) &&
                            it != null && it.isPlanetCategory && !it.excludeFromPlanetCluster)
                        {
                            // 聚簇奖励：要求行星自身启用（格位等级≥0，未被禁用）
                            int idx = ny * ctx.width + nx;
                            if (!disabled[idx] && ((level[idx] + it.enchant) * ctx.mysticFactor[idx]) >= 0)
                            {
                                adjacentPlanets++;
                            }
                        }
                    }
                    if (adjacentPlanets > 0)
                    {
                        score += plugin.PlanetBonus.Value * adjacentPlanets;
                    }
                }
            }

            // 和谐之晶：周围8格护符的有效等级之和越高，伤害放大越大（须启用才生效）
            if (plugin.HarmonyLevelBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot hs = slots[cell];
                    if (hs == null || !hs.hasItem || hs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(hs.instanceID, out ItemInfo harmony) || harmony == null || !harmony.isHarmonyCrystal)
                    {
                        continue;
                    }
                    // 启用检查（与护符一致）
                    bool hEnabled = !disabled[cell] &&
                                    ((level[cell] + harmony.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                    CriteriaSatisfied(ctx, harmony, slots, ignore[cell], cell) &&
                                    (!harmony.slot.charm.isWeaponRelatedCharm ||
                                     (harmony.slot.charm.WeaponController != null &&
                                      harmony.slot.charm.WeaponController.currentWeapon != null &&
                                      harmony.slot.charm.WeaponController.currentWeapon.weaponType == harmony.slot.charm.relatedWeapon));
                    if (!hEnabled)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    int levelSum = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + Neighbor8[i].x;
                        int ny = y + Neighbor8[i].y;
                        Slot neighbor = At(slots, nx, ny, ctx.width, ctx.storage);
                        if (neighbor == null || !neighbor.hasItem || neighbor.charm == null)
                        {
                            continue;
                        }
                        if (!ctx.itemByInstance.TryGetValue(neighbor.instanceID, out ItemInfo ni) || ni == null || ni.isBurden)
                        {
                            continue;
                        }
                        int nIdx = ny * ctx.width + nx;
                        int nLvl = (level[nIdx] + ni.enchant) * ctx.mysticFactor[nIdx];
                        int eff = Mathf.Clamp(nLvl, 0, ni.maxLevel);
                        levelSum += eff;
                    }
                    if (levelSum > 0)
                    {
                        score += plugin.HarmonyLevelBonus.Value * levelSum;
                    }
                }
            }

            // 奉献徽章：同一横排内的同伴(ICompanionCharm)越多，加成越大（徽章须启用）
            if (plugin.DedicationCompanionBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot ds = slots[cell];
                    if (ds == null || !ds.hasItem || ds.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(ds.instanceID, out ItemInfo badge) || badge == null || !badge.isDedicationBadge)
                    {
                        continue;
                    }
                    bool bEnabled = !disabled[cell] &&
                                    ((level[cell] + badge.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                    CriteriaSatisfied(ctx, badge, slots, ignore[cell], cell) &&
                                    (!badge.slot.charm.isWeaponRelatedCharm ||
                                     (badge.slot.charm.WeaponController != null &&
                                      badge.slot.charm.WeaponController.currentWeapon != null &&
                                      badge.slot.charm.WeaponController.currentWeapon.weaponType == badge.slot.charm.relatedWeapon));
                    if (!bEnabled)
                    {
                        continue;
                    }
                    int row = cell / ctx.width;
                    int companions = 0;
                    int rowStart = row * ctx.width;
                    int rowEnd = Math.Min(storage, rowStart + ctx.width);
                    for (int c = rowStart; c < rowEnd; c++)
                    {
                        if (c == cell)
                        {
                            continue;
                        }
                        Slot co = slots[c];
                        if (co == null || !co.hasItem || co.charm == null)
                        {
                            continue;
                        }
                        if (ctx.itemByInstance.TryGetValue(co.instanceID, out ItemInfo ci) &&
                            ci != null && ci.isDedicationCompanion)
                        {
                            companions++;
                        }
                    }
                    if (companions > 0)
                    {
                        score += plugin.DedicationCompanionBonus.Value * companions;
                    }
                }
            }

            // 发光的沙漏（Charm_RightSpellCooldownHelper）：右边一格是魔法书(Charm_Magic)时，
            // 魔法书 CD 恢复速度 +30/60/100%（按沙漏等级）。评分奖励按魔法书 CD 秒数——
            // CD 越长收益越大，搜索会自动把沙漏放到 CD 最长的魔法书左边（沙漏须启用）。
            if (plugin.HourglassBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot gs = slots[cell];
                    if (gs == null || !gs.hasItem || gs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(gs.instanceID, out ItemInfo hour) || hour == null || !hour.isHourglass)
                    {
                        continue;
                    }
                    bool gEnabled = !disabled[cell] &&
                                    ((level[cell] + hour.enchant) * ctx.mysticFactor[cell]) >= 0 &&
                                    CriteriaSatisfied(ctx, hour, slots, ignore[cell], cell) &&
                                    (!hour.slot.charm.isWeaponRelatedCharm ||
                                     (hour.slot.charm.WeaponController != null &&
                                      hour.slot.charm.WeaponController.currentWeapon != null &&
                                      hour.slot.charm.WeaponController.currentWeapon.weaponType == hour.slot.charm.relatedWeapon));
                    if (!gEnabled)
                    {
                        continue;
                    }
                    int gx = cell % ctx.width;
                    int gy = cell / ctx.width;
                    Slot right = At(slots, gx + 1, gy, ctx.width, ctx.storage);
                    if (right == null || !right.hasItem || right.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(right.instanceID, out ItemInfo magic) ||
                        magic == null || !magic.isMagicBook)
                    {
                        continue;
                    }
                    score += plugin.HourglassBonus.Value * Math.Max(0f, magic.magicCd);
                }
            }

            // 指北针：上方为伤害类藏品或另一块指北针时生效（可链式叠加）。
            // 注意：游戏 OnRequestCharmDamageBonus 无 IsEffectEnabled 检查——指北针即使在负等级格上也给上方藏品加成，
            // 因此配对判定不要求指北针自身启用。
            if (plugin.CompassBonus.Value > 0f)
            {
                for (int cell = 0; cell < storage; cell++)
                {
                    Slot cs = slots[cell];
                    if (cs == null || !cs.hasItem || cs.charm == null)
                    {
                        continue;
                    }
                    if (!ctx.itemByInstance.TryGetValue(cs.instanceID, out ItemInfo compass) || compass == null || !compass.isCompass)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    Slot above = At(slots, x, y - 1, ctx.width, ctx.storage);
                    if (above != null && above.hasItem && above.charm != null)
                    {
                        bool valid = above.charm is Charm_UpCharmDamage ||
                                     (above.charm is IAttackableCharm ac && ac.IsAttackableCharm());
                        if (valid)
                        {
                            // 按上方伤害藏品的优先级加权：高优先级伤害神器优先获得指北针加成
                            double w = 1.0;
                            if (ctx.itemByInstance.TryGetValue(above.instanceID, out ItemInfo aboveInfo) && aboveInfo != null)
                            {
                                w = PriorityWeight(aboveInfo.priority);
                            }
                            score += plugin.CompassBonus.Value * w;
                        }
                    }
                }
            }

            return score;
        }

        /// <summary>稀有度 → 用户优先级（1最高~4最低）：传说/羁绊(永恒)=1 稀有=2 高级=3 普通=4。</summary>
        private int RarityToPriority(EItemRarity rarity)
        {
            switch (rarity)
            {
                case EItemRarity.Legend: return Mathf.Clamp(plugin.PriorityLegend.Value, 1, 4);
                case EItemRarity.Eternal: return Mathf.Clamp(plugin.PriorityEternal.Value, 1, 4);
                case EItemRarity.Rare: return Mathf.Clamp(plugin.PriorityRare.Value, 1, 4);
                case EItemRarity.Uncommon: return Mathf.Clamp(plugin.PriorityUncommon.Value, 1, 4);
                default: return Mathf.Clamp(plugin.PriorityCommon.Value, 1, 4);
            }
        }

        /// <summary>优先级 → 等级分权重（1级最高）。</summary>
        private double PriorityWeight(int priority)
        {
            if (!plugin.PriorityEnable.Value)
            {
                return 1.0;
            }
            switch (priority)
            {
                case 1: return plugin.PriorityWeight1.Value;
                case 2: return plugin.PriorityWeight2.Value;
                case 3: return plugin.PriorityWeight3.Value;
                default: return plugin.PriorityWeight4.Value;
            }
        }

        /// <summary>输出完整布局网格图（物品类型缩写 + 格位等级），用于定位摆放问题。</summary>
        private void LogLayoutGrid(SearchContext ctx, List<Slot> slots, string tag)
        {
            EvaluateLayout(ctx, slots); // 刷新等级缓冲
            var sb = new System.Text.StringBuilder();
            sb.Append($"{tag} 布局图(等级/物品)：");
            for (int y = 0; y < ctx.height; y++)
            {
                for (int x = 0; x < ctx.width; x++)
                {
                    int cell = y * ctx.width + x;
                    if (cell >= ctx.storage)
                    {
                        break;
                    }
                    int lvl = ctx.cellLevel[cell];
                    char kind = '.';
                    Slot s = slots[cell];
                    if (s != null && s.hasItem)
                    {
                        if (s.tablet != null) kind = 'T';
                        else if (s.charm != null)
                        {
                            if (ctx.itemByInstance.TryGetValue(s.instanceID, out ItemInfo it) && it != null)
                            {
                                if (it.isBurden) kind = 'B';
                                else if (it.isPlanetModule) kind = 'M';
                                else if (it.isCompass) kind = 'C';
                                else if (it.isPlanetCategory) kind = 'P';
                                else kind = 'c';
                            }
                            else kind = 'c';
                        }
                        else kind = 'o';
                    }
                    sb.Append($"[{lvl,2}{kind}]");
                }
                sb.Append(" | ");
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>输出最终布局中特殊机制的落地情况（望远镜聚星/罗盘配对/负担负格），用于验证。</summary>
        private static void LogLayoutAnalysis(SearchContext ctx, List<Slot> slots, string tag)
        {
            int w = ctx.width;

            for (int cell = 0; cell < ctx.storage; cell++)
            {
                Slot s = slots[cell];
                if (s == null || !s.hasItem)
                {
                    continue;
                }
                if (!ctx.itemByInstance.TryGetValue(s.instanceID, out ItemInfo it) || it == null)
                {
                    continue;
                }
                int x = cell % w;
                int y = cell / w;

                if (it.isPlanetModule)
                {
                    int planets = 0;
                    string list = "";
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + Neighbor8[i].x;
                        int ny = y + Neighbor8[i].y;
                        Slot o = At(slots, nx, ny, w, ctx.storage);
                        if (o != null && o.hasItem &&
                            ctx.itemByInstance.TryGetValue(o.instanceID, out ItemInfo oi) &&
                            oi != null && oi.isPlanetCategory)
                        {
                            planets++;
                            list += $"({nx},{ny}) ";
                        }
                    }
                    Plugin.Log.LogInfo($"{tag} 望远镜@{x},{y}：相邻行星 {planets} 颗 {list.Trim()}");
                }

                if (it.isHarmonyCrystal)
                {
                    int levelSum = 0;
                    string list = "";
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + Neighbor8[i].x;
                        int ny = y + Neighbor8[i].y;
                        Slot o = At(slots, nx, ny, w, ctx.storage);
                        if (o != null && o.hasItem && o.charm != null &&
                            ctx.itemByInstance.TryGetValue(o.instanceID, out ItemInfo oi) && oi != null)
                        {
                            int oIdx = ny * w + nx;
                            int eff = Mathf.Clamp((ctx.cellLevel[oIdx] + oi.enchant) * ctx.mysticFactor[oIdx], 0, oi.maxLevel);
                            levelSum += eff;
                            list += $"({nx},{ny}:{eff}) ";
                        }
                    }
                    Plugin.Log.LogInfo($"{tag} 和谐之晶@{x},{y}：周围8格等级和={levelSum} {list.Trim()}");
                }

                if (it.isDedicationBadge)
                {
                    int companions = 0;
                    string list = "";
                    for (int i = 0; i < w; i++)
                    {
                        int idx = y * w + i;
                        if (idx >= ctx.storage || idx == cell)
                        {
                            continue;
                        }
                        Slot o = slots[idx];
                        if (o != null && o.hasItem &&
                            ctx.itemByInstance.TryGetValue(o.instanceID, out ItemInfo oi) &&
                            oi != null && oi.isDedicationCompanion)
                        {
                            companions++;
                            list += $"({i},{y}) ";
                        }
                    }
                    Plugin.Log.LogInfo($"{tag} 奉献徽章@{x},{y}：同行同伴 {companions} 个 {list.Trim()}");
                }

                if (it.isHourglass)
                {
                    Slot right = At(slots, x + 1, y, w, ctx.storage);
                    if (right != null && right.hasItem && right.charm is Charm_Magic)
                    {
                        float cd = 0f;
                        if (ctx.itemByInstance.TryGetValue(right.instanceID, out ItemInfo ri) && ri != null)
                        {
                            cd = ri.magicCd;
                        }
                        Plugin.Log.LogInfo($"{tag} 沙漏@{x},{y}：右边魔法书 CD={cd:F1}s");
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"{tag} 沙漏@{x},{y}：右边无魔法书（未配对）");
                    }
                }

                if (it.isCompass)
                {
                    string above = "空";
                    string abovePrio = "";
                    Slot a = At(slots, x, y - 1, w, ctx.storage);
                    if (a != null && a.hasItem && a.charm != null)
                    {
                        above = a.charm is Charm_UpCharmDamage ? "指北针" :
                                (a.charm is IAttackableCharm ac && ac.IsAttackableCharm()) ? "伤害类" : "其他";
                        if (ctx.itemByInstance.TryGetValue(a.instanceID, out ItemInfo ai) && ai != null)
                        {
                            abovePrio = $" P{ai.priority}";
                        }
                    }
                    Plugin.Log.LogInfo($"{tag} 罗盘@{x},{y}：上方={above}{abovePrio}");
                }

                if (it.isBurden)
                {
                    int lvl = ctx.cellLevel[cell];
                    Plugin.Log.LogInfo($"{tag} 负担@{x},{y}：格等级={lvl}（{(lvl < 0 ? "负格✓" : "非负格")}）");
                }
            }
        }

        /// <summary>输出本次整理识别到的特殊物品统计（望远镜/罗盘/负担/附魔/稀有度），便于排查与验证。</summary>
        private static void LogItemIdentification(SearchContext ctx)
        {
            int telescopes = 0, compasses = 0, burdens = 0, attackable = 0, planets = 0, enchanted = 0, enchantSum = 0;
            int weaponRelated = 0, weaponMatched = 0;
            int dedicationBadges = 0, dedicationCompanions = 0;
            int hourglasses = 0, magicBooks = 0;
            int[] rarityCount = new int[5];
            int[] priorityCount = new int[5];
            foreach (ItemInfo it in ctx.items)
            {
                if (it.isBurden) burdens++;
                if (it.isPlanetModule) telescopes++;
                if (it.isCompass) compasses++;
                if (it.isAttackable && !it.isCompass) attackable++;
                if (it.isPlanetCategory) planets++;
                if (it.enchant != 0) { enchanted++; enchantSum += it.enchant; }
                if (it.isDedicationBadge) dedicationBadges++;
                if (it.isDedicationCompanion) dedicationCompanions++;
                if (it.isHourglass) hourglasses++;
                if (it.isMagicBook) magicBooks++;
                if (it.isCharm && !it.isBurden)
                {
                    rarityCount[(int)it.rarity]++;
                    if (it.priority >= 1 && it.priority <= 4) priorityCount[it.priority]++;
                    if (it.slot.charm.isWeaponRelatedCharm)
                    {
                        weaponRelated++;
                        var wc = it.slot.charm.WeaponController;
                        if (wc != null && wc.currentWeapon != null &&
                            wc.currentWeapon.weaponType == it.slot.charm.relatedWeapon)
                        {
                            weaponMatched++;
                        }
                    }
                }
            }
            Plugin.Log.LogInfo(
                $"识别：存储{ctx.storage}格({ctx.width}x{ctx.height}) 石板{ctx.steles.Count} 护符{ctx.charms.Count}" +
                $" 望远镜{telescopes} 罗盘{compasses} 伤害类{attackable} 行星类{planets} 负担{burdens}" +
                $" 神秘{ctx.mysticCount}个/×2地块{ctx.mysticActiveCells}格" +
                $" 附魔{enchanted}件(+{enchantSum}) 武器相关{weaponRelated}(匹配{weaponMatched})" +
                $" 奉献徽章{dedicationBadges} 同伴{dedicationCompanions}" +
                $" 沙漏{hourglasses} 魔法书{magicBooks}" +
                $" 优先级[P1:{priorityCount[1]} P2:{priorityCount[2]} P3:{priorityCount[3]} P4:{priorityCount[4]}]" +
                $" 稀有度[普通{rarityCount[0]} 优秀{rarityCount[1]} 稀有{rarityCount[2]} 传说{rarityCount[3]} 永恒{rarityCount[4]}]");

            // 诊断：打印护符的实际标签（排查标签名是否与配置一致）
            var tagList = new System.Text.StringBuilder();
            int shown = 0;
            foreach (ItemInfo it in ctx.charms)
            {
                if (it.entity != null && it.entity.categories != null && it.entity.categories.Count > 0 && shown < 6)
                {
                    tagList.Append($" [{it.entity.aName?.key}→{string.Join("/", it.entity.categories)}]");
                    shown++;
                }
            }
            if (tagList.Length > 0)
            {
                Plugin.Log.LogInfo($"护符标签{tagList}");
            }
        }

        // ---------------------------------------------------------------- 智能初始布局

        private List<Slot> BuildSmartStart(SearchContext ctx)
        {
            int storage = ctx.storage;
            Slot[] result = new Slot[storage];
            bool[] occupied = new bool[storage];

            // 1) 石板：有负效果的优先摆，逐格逐旋转打分（含条件检查）
            var steles = new List<ItemInfo>(ctx.steles);
            steles.Sort((a, b) => SteleImportance(b.slot).CompareTo(SteleImportance(a.slot)));
            foreach (ItemInfo stele in steles)
            {
                int bestIdx = -1;
                int bestRot = stele.slot.rotation;
                float bestScore = float.MinValue;

                int rotations = DungeonManager.IsTabletRotatable(stele.slot.tablet.instanceID, stele.slot.tablet.isRotatable) ? 4 : 1;
                for (int cell = 0; cell < storage; cell++)
                {
                    if (occupied[cell])
                    {
                        continue;
                    }
                    for (int rot = 0; rot < rotations; rot++)
                    {
                        if (ctx.stelePatterns.TryGetValue(stele.index, out var byCell) &&
                            byCell.TryGetValue(cell * 4 + rot, out var pattern))
                        {
                            float sc = EvaluateStelePattern(ctx, pattern, result, occupied);
                            if (sc > bestScore)
                            {
                                bestScore = sc;
                                bestIdx = cell;
                                bestRot = rot;
                            }
                        }
                    }
                }

                if (bestIdx >= 0)
                {
                    stele.slot.rotation = bestRot;
                    result[bestIdx] = stele.slot;
                    occupied[bestIdx] = true;
                }
            }

            // 预评布局（用于给护符选格）：先评估一次，刷新等级/禁用/豁免缓冲
            var slotsNow = SlotsFromArray(result, storage);
            EvaluateLayout(ctx, slotsNow);

            // 2) 护符放置顺序：受限护符(位置条件) → 望远镜 → 行星聚簇 → 罗盘配对 → 其余(稀有度)
            var remaining = new List<ItemInfo>(ctx.charms);

            // 2a) 位置条件受限的护符（必须满足条件才生效；同条件等级内按用户优先级）
            var restricted = remaining.FindAll(x => KindPriority(x.kind) >= 2);
            restricted.Sort((a, b) =>
            {
                int p = KindPriority(b.kind).CompareTo(KindPriority(a.kind));
                if (p != 0)
                {
                    return p;
                }
                return a.priority.CompareTo(b.priority);
            });
            foreach (ItemInfo charm in restricted)
            {
                int cell = FindBestCharmCell(ctx, charm, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = charm.slot;
                    occupied[cell] = true;
                    remaining.Remove(charm);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2a2) 行锁定物品（如凯尔萨德尼钥匙）：放回用户所在行，行内最佳列（FindBestCharmCell 已限行）
            var rowLocked = remaining.FindAll(x => x.isRowLocked);
            foreach (ItemInfo rl in rowLocked)
            {
                int cell = FindBestCharmCell(ctx, rl, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFreeInRow(occupied, rl.lockRow, ctx);
                }
                if (cell >= 0)
                {
                    result[cell] = rl.slot;
                    occupied[cell] = true;
                    remaining.Remove(rl);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2b) 行星望远镜
            int telescopeCell = -1;
            ItemInfo telescope = remaining.Find(x => x.isPlanetModule);
            if (telescope != null)
            {
                int cell = FindBestCharmCell(ctx, telescope, result, occupied, slotsNow);
                if (cell >= 0)
                {
                    result[cell] = telescope.slot;
                    occupied[cell] = true;
                    telescopeCell = cell;
                    remaining.Remove(telescope);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2b2) 和谐之晶：先放（周围8格等级和→伤害放大），其8邻域格加权吸引高等级护符
            var harmonyNeighbors = new HashSet<int>();
            var harmonyCrystals = remaining.FindAll(x => x.isHarmonyCrystal);
            foreach (ItemInfo hc in harmonyCrystals)
            {
                int cell = FindBestCharmCell(ctx, hc, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = hc.slot;
                    occupied[cell] = true;
                    remaining.Remove(hc);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                    // 收集8邻域格（未占用的）
                    int hx = cell % ctx.width;
                    int hy = cell / ctx.width;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = hx + Neighbor8[i].x;
                        int ny = hy + Neighbor8[i].y;
                        if (InBounds(nx, ny, ctx.width, ctx.height))
                        {
                            int idx = ny * ctx.width + nx;
                            if (idx >= 0 && idx < ctx.storage && !occupied[idx])
                            {
                                harmonyNeighbors.Add(idx);
                            }
                        }
                    }
                }
            }

            // 2b3) 奉献徽章（Charm_CompanionChaos）：先放，记录其横排；同行空格加权引导同伴同排（金色手铃/迷你弩炮/灵魂粉末/采矿臂章）
            int dedicationRow = -1;
            var dedicationBadges = remaining.FindAll(x => x.isDedicationBadge);
            foreach (ItemInfo db in dedicationBadges)
            {
                int cell = FindBestCharmCell(ctx, db, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = db.slot;
                    occupied[cell] = true;
                    dedicationRow = cell / ctx.width;
                    remaining.Remove(db);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2b4) 发光的沙漏（Charm_RightSpellCooldownHelper）：先放（自动避开最右列），记录其右边格，
            //      引导魔法书（CD 越长越优先）就位到沙漏右边
            var hourglassRightCells = new HashSet<int>();
            var hourglasses = remaining.FindAll(x => x.isHourglass);
            foreach (ItemInfo hg in hourglasses)
            {
                int cell = FindBestCharmCell(ctx, hg, result, occupied, slotsNow);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = hg.slot;
                    occupied[cell] = true;
                    remaining.Remove(hg);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                    int hx = cell % ctx.width;
                    int hy = cell / ctx.width;
                    int ridx = hy * ctx.width + (hx + 1);
                    if (hx + 1 < ctx.width && ridx >= 0 && ridx < ctx.storage && !occupied[ridx])
                    {
                        hourglassRightCells.Add(ridx);
                    }
                }
            }

            // 2c) 行星藏品聚到望远镜周围（望远镜已放置时）
            if (telescopeCell >= 0)
            {
                int tx = telescopeCell % ctx.width;
                int ty = telescopeCell / ctx.width;
                var planets = remaining.FindAll(x => x.isPlanetCategory && !x.excludeFromPlanetCluster);
                foreach (ItemInfo planet in planets)
                {
                    // 找望远镜相邻且空闲的格子，等级高者优先；跳过负等级/禁用格（行星需要启用才吃到聚簇奖励）
                    int bestAdj = -1;
                    float bestAdjScore = float.MinValue;
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = tx + Neighbor8[i].x;
                        int ny = ty + Neighbor8[i].y;
                        if (!InBounds(nx, ny, ctx.width, ctx.height))
                        {
                            continue;
                        }
                        int idx = ny * ctx.width + nx;
                        if (idx >= 0 && idx < ctx.storage &&
                            !occupied[idx] && ctx.cellLevel[idx] >= 0 && !ctx.disabled[idx])
                        {
                            float sc = ctx.cellLevel[idx] * 100f;
                            if (sc > bestAdjScore)
                            {
                                bestAdjScore = sc;
                                bestAdj = idx;
                            }
                        }
                    }
                    int target = bestAdj >= 0 ? bestAdj : FindBestCharmCell(ctx, planet, result, occupied, slotsNow, harmonyNeighbors, dedicationRow);
                    if (target < 0)
                    {
                        target = FirstFree(occupied);
                    }
                    if (target >= 0)
                    {
                        result[target] = planet.slot;
                        occupied[target] = true;
                        remaining.Remove(planet);
                        slotsNow = SlotsFromArray(result, storage);
                        EvaluateLayout(ctx, slotsNow);
                    }
                }
            }

            // 2d) 指北针配对：放在"上方是伤害类/指北针"的格子（优先），否则普通选格
            var compasses = remaining.FindAll(x => x.isCompass);
            foreach (ItemInfo compass in compasses)
            {
                int target = FindCompassTargetCell(ctx, result, occupied);
                if (target < 0)
                {
                    target = FindBestCharmCell(ctx, compass, result, occupied, slotsNow, harmonyNeighbors, dedicationRow);
                }
                if (target < 0)
                {
                    target = FirstFree(occupied);
                }
                if (target >= 0)
                {
                    result[target] = compass.slot;
                    occupied[target] = true;
                    remaining.Remove(compass);
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 2e) 其余护符按用户优先级（1→4），同优先级内按稀有度
            remaining.Sort((a, b) =>
            {
                int p = a.priority.CompareTo(b.priority);
                if (p != 0)
                {
                    return p;
                }
                return b.rarity.CompareTo(a.rarity);
            });
            foreach (ItemInfo charm in remaining)
            {
                int cell = FindBestCharmCell(ctx, charm, result, occupied, slotsNow, harmonyNeighbors, dedicationRow, hourglassRightCells);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = charm.slot;
                    occupied[cell] = true;
                    slotsNow = SlotsFromArray(result, storage);
                    EvaluateLayout(ctx, slotsNow);
                }
            }

            // 3) 其余物品填空
            foreach (ItemInfo other in ctx.others)
            {
                int cell = FirstFree(occupied);
                if (cell >= 0)
                {
                    result[cell] = other.slot;
                    occupied[cell] = true;
                }
            }

            // 4) 负面藏品：塞进最差（负等级最高）的格子
            foreach (ItemInfo burden in ctx.burdens)
            {
                int cell = FindWorstCell(ctx, result, occupied);
                if (cell < 0)
                {
                    cell = FirstFree(occupied);
                }
                if (cell >= 0)
                {
                    result[cell] = burden.slot;
                    occupied[cell] = true;
                }
            }

            return SlotsFromArray(result, storage);
        }

        /// <summary>找"上方是伤害类/指北针"的空格（罗盘配对目标）；上方伤害藏品优先级越高越优先。</summary>
        private int FindCompassTargetCell(SearchContext ctx, Slot[] result, bool[] occupied)
        {
            int best = -1;
            float bestScore = float.MinValue;
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (occupied[cell])
                {
                    continue;
                }
                int x = cell % ctx.width;
                int y = cell / ctx.width;
                int ax = x;
                int ay = y - 1;
                if (ay < 0)
                {
                    continue;
                }
                int idx = ay * ctx.width + ax;
                Slot above = null;
                if (idx >= 0 && idx < ctx.storage)
                {
                    above = result[idx];
                }
                if (above != null && above.hasItem && above.charm != null)
                {
                    bool valid = above.charm is Charm_UpCharmDamage ||
                                 (above.charm is IAttackableCharm ac && ac.IsAttackableCharm());
                    if (valid)
                    {
                        // 按上方伤害藏品的优先级加权：高优先级伤害神器优先配对
                        float w = 1f;
                        if (ctx.itemByInstance.TryGetValue(above.instanceID, out ItemInfo aboveInfo) && aboveInfo != null)
                        {
                            w = (float)PriorityWeight(aboveInfo.priority);
                        }
                        float sc = ctx.cellLevel[cell] * 100f * w - (ctx.disabled[cell] ? 500f : 0f);
                        if (sc > bestScore)
                        {
                            bestScore = sc;
                            best = cell;
                        }
                    }
                }
            }
            return best;
        }

        private static int SteleImportance(Slot stele)
        {
            if (stele.tablet == null)
            {
                return 0;
            }
            try
            {
                string q = stele.tablet.GetQuery(stele.tablet.instanceID);
                if (string.IsNullOrEmpty(q))
                {
                    return 0;
                }
                int negatives = 0;
                foreach (string line in q.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split(' ');
                    if (parts.Length >= 2 && int.TryParse(parts[parts.Length - 1], out int v) && v < 0)
                    {
                        negatives++;
                    }
                }
                return negatives;
            }
            catch
            {
                return 0;
            }
        }

        private static float EvaluateStelePattern(SearchContext ctx, StelePattern pattern, Slot[] result, bool[] occupied)
        {
            if (!ConditionsOk(pattern, SlotsFromArray(result, ctx.storage)))
            {
                return -1000f;
            }

            float score = 0f;
            foreach (EffectEntry e in pattern.effects)
            {
                bool covered = e.cell >= 0 && e.cell < ctx.storage && (occupied[e.cell] || result[e.cell] != null);
                switch (e.kind)
                {
                    case 0:
                        score += e.value * 10f;
                        if (e.value < 0 && !covered)
                        {
                            score -= 160f;
                        }
                        break;
                    case 2:
                        score += 25f;
                        break;
                    case 1:
                        score += 1f;
                        break;
                }
            }
            return score;
        }

        /// <summary>为护符挑选最佳格子：有满足条件(或豁免)的格子时只在这些格里选等级最高的；否则退而求其次。</summary>
        private static int FindBestCharmCell(SearchContext ctx, ItemInfo charm, Slot[] result, bool[] occupied,
            List<Slot> slotsNow, HashSet<int> harmonyNeighbors = null, int dedicationRow = -1,
            HashSet<int> hourglassRightCells = null)
        {
            // 分档候选：自然满足位置格 / 豁免格(IgnoreCriteria) / 任意格
            int bestNatural = -1;
            float bestNaturalScore = float.MinValue;
            int bestIgnore = -1;
            float bestIgnoreScore = float.MinValue;
            int bestAny = -1;
            float bestAnyScore = float.MinValue;

            bool restricted = KindPriority(charm.kind) >= 2;

            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (occupied[cell])
                {
                    continue;
                }
                int x = cell % ctx.width;
                int y = cell / ctx.width;
                if (charm.isRowLocked && y != charm.lockRow)
                {
                    continue; // 行锁定：只能在本行内调整
                }
                if (charm.isHourglass && x == ctx.width - 1)
                {
                    continue; // 沙漏：最右列右边没有格子，永远配不上魔法书
                }
                bool isIgnore = ctx.ignore[cell];
                bool natural = IsSatisfyingCell(ctx.inv, charm.kind, x, y, cell, ctx.storage, ctx.width);
                int level = ctx.cellLevel[cell];
                // 和谐之晶邻域：高等级护符优先聚到它周围8格
                float sc = level * 100f * ctx.mysticFactor[cell] - (ctx.disabled[cell] ? 500f : 0f);
                if (harmonyNeighbors != null && harmonyNeighbors.Contains(cell))
                {
                    sc += 8000f;
                }
                if (dedicationRow >= 0 && y == dedicationRow)
                {
                    sc += 6000f; // 奉献徽章同行：同伴优先同横排
                }
                if (charm.isMagicBook && hourglassRightCells != null && hourglassRightCells.Contains(cell))
                {
                    sc += 8000f + charm.magicCd * 4000f; // 沙漏右边格：魔法书优先，CD 越长越优先
                }
                if (ctx.mysticFactor[cell] > 1)
                {
                    sc += 20000f;
                }
                if (restricted)
                {
                    if (charm.preferIgnoreCells)
                    {
                        // 冰锁类：豁免格优先（+5000，可解除限制上高等级格）
                        if (isIgnore)
                        {
                            sc += 5000f;
                        }
                        else if (natural)
                        {
                            sc += 500f; // 自然限制位置作为次选
                        }
                    }
                    else if (natural && !isIgnore)
                    {
                        // 普通受限物品：自然满足位置优先（小偏好），豁免格作为后备
                        sc += 500f;
                    }
                }

                if (sc > bestAnyScore)
                {
                    bestAnyScore = sc;
                    bestAny = cell;
                }
                if (natural && !isIgnore && sc > bestNaturalScore)
                {
                    bestNaturalScore = sc;
                    bestNatural = cell;
                }
                if (isIgnore && sc > bestIgnoreScore)
                {
                    bestIgnoreScore = sc;
                    bestIgnore = cell;
                }
            }

            if (restricted)
            {
                if (charm.preferIgnoreCells)
                {
                    // 冰锁类：豁免格 > 自然位置 > 任意
                    if (bestIgnore >= 0) return bestIgnore;
                    if (bestNatural >= 0) return bestNatural;
                }
                else
                {
                    // 普通受限：自然位置 > 豁免格 > 任意
                    if (bestNatural >= 0) return bestNatural;
                    if (bestIgnore >= 0) return bestIgnore;
                }
            }
            return bestAny;
        }

        private static int FindWorstCell(SearchContext ctx, Slot[] result, bool[] occupied)
        {
            int worst = -1;
            int worstLevel = int.MaxValue;
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (occupied[cell])
                {
                    continue;
                }
                int level = ctx.cellLevel[cell];
                if (level < worstLevel)
                {
                    worstLevel = level;
                    worst = cell;
                }
            }
            return worst;
        }

        private static int FirstFree(bool[] occupied)
        {
            for (int i = 0; i < occupied.Length; i++)
            {
                if (!occupied[i])
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>指定行内第一个空闲格子（行锁定物品用）。</summary>
        private static int FirstFreeInRow(bool[] occupied, int row, SearchContext ctx)
        {
            int start = row * ctx.width;
            int end = Math.Min(ctx.storage, start + ctx.width);
            for (int i = start; i < end; i++)
            {
                if (!occupied[i])
                {
                    return i;
                }
            }
            return -1;
        }

        private static List<Slot> SlotsFromArray(Slot[] result, int storage)
        {
            var list = new List<Slot>(storage);
            for (int i = 0; i < storage; i++)
            {
                list.Add(result[i] != null ? result[i] : Slot.Empty());
            }
            return list;
        }

        /// <summary>防御：校验 CaptureState 快照与背包当前状态一致（物品实例集合相同），
        /// 防止背包初始化未完成时按不完整快照清空重写导致物品丢失。
        /// 注意：只统计"正常背包格"（y 在高度内且索引在 storage 内），排除药水带(y=100)等特殊位置。</summary>
        private static bool VerifyInventorySnapshot(GridInventory inv, List<Slot> captured)
        {
            try
            {
                int storage = inv.CurrentInventoryStorage;
                int height = inv.GetHeight(storage);

                var fresh = new HashSet<int>();
                foreach (var kv in inv.inventoryMatrix)
                {
                    if (kv.Value == null)
                    {
                        continue;
                    }
                    if (kv.Key.y < 0 || kv.Key.y >= height)
                    {
                        continue; // 药水带(y=100)等特殊位置，不参与背包整理
                    }
                    int idx = inv.PosToIdx(kv.Key);
                    if (idx < 0 || idx >= storage)
                    {
                        continue;
                    }
                    fresh.Add(kv.Value.InstanceID);
                }

                var capturedIds = new HashSet<int>();
                foreach (Slot s in captured)
                {
                    if (s != null && s.hasItem)
                    {
                        capturedIds.Add(s.instanceID);
                    }
                }

                if (fresh.Count != capturedIds.Count)
                {
                    return false;
                }
                foreach (int id in fresh)
                {
                    if (!capturedIds.Contains(id))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 入口

        public void Sort()
        {
            if (busy)
            {
                Plugin.Log.LogWarning("整理正在进行中，请稍候再试。");
                return;
            }

            if (!NetworkClient.active)
            {
                Notify("未进入游戏会话，无法整理背包。");
                return;
            }

            var localPlayer = NetworkClient.localPlayer;
            if (localPlayer == null)
            {
                Notify("未找到本地玩家，无法整理背包。");
                return;
            }

            var avatar = localPlayer.GetComponent<PlayerAvatar>();
            if (avatar == null)
            {
                Notify("未找到玩家角色，无法整理背包。");
                return;
            }

            var inv = avatar.Inventory;
            if (inv == null)
            {
                Notify("背包不可用，无法整理。");
                return;
            }

            // 防御：会话刚建立时背包初始化未完成，此刻整理可能按不完整快照清空重写导致物品丢失。
            // 记录会话稳定时刻：本地玩家出现后经过稳定延迟才允许整理。
            if (sessionStartTime < 0)
            {
                sessionStartTime = Time.unscaledTime;
            }
            if (Time.unscaledTime - sessionStartTime < plugin.SessionStableDelay.Value)
            {
                Notify($"背包初始化中，请稍候 {Mathf.CeilToInt(plugin.SessionStableDelay.Value)} 秒后再整理。");
                return;
            }

            if (inv.CurrentInventoryStorage <= 1 || inv.charms.Count == 0)
            {
                Notify("背包为空或没有护符，无需整理。");
                return;
            }

            busy = true;
            try
            {
                if (plugin.SelfTest.Value && NetworkServer.active)
                {
                    SortSelfTest(inv);
                    return;
                }

                switch (plugin.Mode.Value)
                {
                    case SortMode.Vanilla:
                        SortVanilla(inv);
                        break;

                    case SortMode.Enhanced:
                        if (NetworkServer.active)
                        {
                            SortEnhanced(inv);
                        }
                        else
                        {
                            // 联机客户端：本地离线算最优布局，用游戏自带网络接口(Swap/DoClickAction)执行
                            SortClient(inv);
                        }
                        break;

                    default:
                        Plugin.Log.LogWarning($"未知整理模式 {plugin.Mode.Value}，使用内置整理。");
                        SortVanilla(inv);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"整理背包异常: {ex}");
                Notify("整理背包失败，详见日志。");
            }
            finally
            {
                busy = false;
            }
        }

        // ---------------------------------------------------------------- Vanilla

        private void SortVanilla(GridInventory inv)
        {
            float before = SafeScore(inv);
            bool knownResult;

            if (NetworkServer.active)
            {
                knownResult = inv.AutoArrangeInventoryForBestCharmLevels(
                    plugin.VanillaIterations.Value, plugin.AllowTabletRotation.Value);
            }
            else
            {
                inv.RequestAutoArrangeInventoryForBestCharmLevels(
                    plugin.VanillaIterations.Value, plugin.AllowTabletRotation.Value);
                knownResult = true;
            }

            if (!knownResult)
            {
                Notify("背包无需整理（无护符或已是最优）。");
                return;
            }

            float after = SafeScore(inv);
            if (float.IsNaN(before) || float.IsNaN(after))
            {
                Notify("整理完毕");
            }
            else
            {
                Plugin.Log.LogInfo($"内置整理完成：加成评分 {before:F0} -> {after:F0}");
                Notify("整理完毕");
            }
        }

        // ---------------------------------------------------------------- Enhanced

        /// <summary>离线计算最优布局（智能初始 + 多轮独立多起点退火，取全局最优），不修改游戏状态。</summary>
        private List<Slot> ComputeBestLayout(GridInventory inv, List<Slot> original, SearchContext ctx,
            out double beforeScore, out double bestScore)
        {
            beforeScore = EvaluateLayout(ctx, original);

            // 智能初始布局
            List<Slot> start;
            if (plugin.EnableSmartStart.Value)
            {
                start = BuildSmartStart(ctx);
                double smartScore = EvaluateLayout(ctx, start);
                Plugin.Log.LogInfo($"智能初始布局：评分 {beforeScore:F0} -> {smartScore:F0}");
            }
            else
            {
                start = original;
            }

            // 多轮独立搜索（不同随机种子），取全局最优——等效于自动重复按 F8 多次
            int rounds = Math.Max(1, plugin.SearchRounds.Value);
            List<Slot> bestLayout = original;
            double globalBest = beforeScore;
            for (int round = 0; round < rounds; round++)
            {
                var rng = new System.Random(Environment.TickCount + round * 7919);
                var result = AnnealMultiStart(ctx, start, original, rng,
                    plugin.EnhancedIterations.Value, plugin.EnhancedRestarts.Value,
                    plugin.EnhancedTemperature.Value);
                if (result.Score > globalBest)
                {
                    globalBest = result.Score;
                    bestLayout = result.Best;
                }
            }
            bestScore = globalBest;

            return globalBest >= beforeScore - 0.5 ? bestLayout : original;
        }

        private void SortEnhanced(GridInventory inv)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            List<Slot> original = CaptureState(inv);
            if (!VerifyInventorySnapshot(inv, original))
            {
                Notify("背包状态未就绪，本次未整理（请稍后再试）。");
                return;
            }
            SearchContext ctx = BuildContext(inv, original);
            LogItemIdentification(ctx);

            List<Slot> finalLayout = ComputeBestLayout(inv, original, ctx, out double beforeScore, out double bestScore);

            // 应用最优布局（仅一次权限周期）
            float finalGameScore = ApplyAndScore(inv, finalLayout);
            double finalOffline = EvaluateLayout(ctx, finalLayout);
            float beforeGame = SafeScoreBefore(inv, original);

            LogLayoutGrid(ctx, finalLayout, "整理");
            LogLayoutAnalysis(ctx, finalLayout, "整理");

            sw.Stop();
            Plugin.Log.LogInfo(
                $"增强整理完成（{sw.ElapsedMilliseconds}ms）：离线评分 {beforeScore:F0} -> {finalOffline:F0}" +
                $"（搜索最优 {bestScore:F0}）；游戏评分 {beforeGame:F0} -> {finalGameScore:F0}" +
                $"；布局 {ctx.items.Count} 件（石板{ctx.steles.Count} 护符{ctx.charms.Count}" +
                $" 负担{ctx.burdens.Count} 其他{ctx.others.Count}）");
            Notify("整理完毕");
        }

        /// <summary>联机客户端智能整理：本地离线算最优布局，再用游戏自带网络接口（Swap 交换 / DoClickAction 旋转）逐步调整。
        /// 无需服务器权限，主机/客户端均可用；不清空背包，比主机版更安全。</summary>
        private void SortClient(GridInventory inv)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            List<Slot> original = CaptureState(inv);
            if (!VerifyInventorySnapshot(inv, original))
            {
                Notify("背包状态未就绪，本次未整理（请稍后再试）。");
                return;
            }
            SearchContext ctx = BuildContext(inv, original);
            LogItemIdentification(ctx);

            List<Slot> finalLayout = ComputeBestLayout(inv, original, ctx, out double beforeScore, out double bestScore);

            // 生成操作序列（逻辑推演：物品位置与旋转随交换更新）
            var swaps = new List<(int a, int b)>();
            var rots = new List<(int pos, int count)>();
            BuildClientOps(inv, original, finalLayout, swaps, rots);

            if (swaps.Count == 0 && rots.Count == 0)
            {
                sw.Stop();
                Plugin.Log.LogInfo($"联机整理：布局已最优，无需调整（{sw.ElapsedMilliseconds}ms）。");
                Notify("整理完毕");
                return;
            }

            // 执行：先交换位置，再旋转石板
            foreach (var (a, b) in swaps)
            {
                ItemPosition pa = inv.IdxToPos(a);
                ItemPosition pb = inv.IdxToPos(b);
                inv.Swap(pa.x, pa.y, pb.x, pb.y);
            }
            foreach (var (pos, count) in rots)
            {
                ItemPosition p = inv.IdxToPos(pos);
                for (int r = 0; r < count; r++)
                {
                    inv.DoClickAction(p);
                }
            }

            sw.Stop();
            Plugin.Log.LogInfo(
                $"联机客户端整理完成（{sw.ElapsedMilliseconds}ms 计算，{swaps.Count} 次交换/{rots.Count} 处旋转）：" +
                $"离线评分 {beforeScore:F0} -> {bestScore:F0}；布局 {ctx.items.Count} 件" +
                $"（石板{ctx.steles.Count} 护符{ctx.charms.Count} 负担{ctx.burdens.Count}）");
            Notify("整理完毕");
        }

        /// <summary>把"当前布局→目标布局"转换为 交换(位置) + 旋转(位置×次数) 操作序列（逻辑推演，含位置与旋转追踪）。</summary>
        private static void BuildClientOps(GridInventory inv, List<Slot> current, List<Slot> target,
            List<(int, int)> swaps, List<(int, int)> rots)
        {
            int n = Math.Min(current.Count, target.Count);
            var logical = CloneSlots(current);
            var posOf = new Dictionary<int, int>();
            for (int i = 0; i < logical.Count; i++)
            {
                if (logical[i].hasItem)
                {
                    posOf[logical[i].instanceID] = i;
                }
            }

            // 交换：逐位置归位（物品集相同；目标空位时把多余物品换到空位）
            for (int i = 0; i < n; i++)
            {
                bool curHas = logical[i].hasItem;
                bool tgtHas = target[i].hasItem;
                int curId = curHas ? logical[i].instanceID : -1;
                int tgtId = tgtHas ? target[i].instanceID : -1;
                if (curHas == tgtHas && curId == tgtId)
                {
                    continue;
                }

                if (tgtHas)
                {
                    // 位置 i 需要放置目标物品（在 j）
                    if (posOf.TryGetValue(tgtId, out int j) && j != i)
                    {
                        swaps.Add((i, j));
                        SwapLogical(logical, posOf, i, j);
                    }
                }
                else
                {
                    // 目标为空：把 i 上物品换到某个空位
                    int empty = -1;
                    for (int k = 0; k < logical.Count; k++)
                    {
                        if (k != i && !logical[k].hasItem)
                        {
                            empty = k;
                            break;
                        }
                    }
                    if (empty >= 0)
                    {
                        swaps.Add((i, empty));
                        SwapLogical(logical, posOf, i, empty);
                    }
                }
            }

            // 旋转：交换完成后，目标位置上的石板若旋转不一致则记录（在最终位置执行点击旋转）
            for (int i = 0; i < n; i++)
            {
                Slot t = target[i];
                Slot l = logical[i];
                if (t.hasItem && t.tablet != null && l.hasItem && l.tablet != null &&
                    t.instanceID == l.instanceID && t.rotation != l.rotation)
                {
                    int count = (t.rotation - l.rotation + 4) % 4;
                    if (count > 0)
                    {
                        rots.Add((i, count));
                    }
                }
            }
        }

        private static void SwapLogical(List<Slot> logical, Dictionary<int, int> posOf, int a, int b)
        {
            Slot tmp = logical[a];
            logical[a] = logical[b];
            logical[b] = tmp;
            if (logical[a].hasItem)
            {
                posOf[logical[a].instanceID] = a;
            }
            if (logical[b].hasItem)
            {
                posOf[logical[b].instanceID] = b;
            }
        }

        private float SafeScoreBefore(GridInventory inv, List<Slot> original)
        {
            // 读取整理前的真实游戏评分（不改变布局）
            return SafeScore(inv);
        }

        private struct AnnealResult
        {
            public List<Slot> Best;
            public double Score;
        }

        /// <summary>全离线模拟退火：交换 / 移动 / 旋转 / 定向移动（条件、行星、罗盘、负担）。</summary>
        private AnnealResult Anneal(SearchContext ctx, List<Slot> start, System.Random rng,
            int iterations, int restarts, float temp0)
        {
            var best = CloneSlots(start);
            double bestScore = EvaluateLayout(ctx, best);

            var candidate = CloneSlots(best);
            double candidateScore = bestScore;

            for (int r = 0; r < restarts; r++)
            {
                for (int i = 0; i < iterations; i++)
                {
                    var mutated = CloneSlots(candidate);
                    Mutate(ctx, mutated, rng);

                    double s = EvaluateLayout(ctx, mutated);

                    if (s > bestScore)
                    {
                        best = CloneSlots(mutated);
                        bestScore = s;
                    }

                    double t = Math.Max(1f, temp0 * (1f - (double)i / Math.Max(1, iterations)));
                    bool accept = s >= candidateScore ||
                                  rng.NextDouble() < Math.Exp((s - candidateScore) / t);
                    if (accept)
                    {
                        candidate = mutated;
                        candidateScore = s;
                    }
                }

                candidateScore = EvaluateLayout(ctx, best);
                candidate = CloneSlots(best);
            }

            return new AnnealResult { Best = best, Score = bestScore };
        }

        /// <summary>多起点退火：智能初始 / 原始布局 / 随机布局各跑一轮，取全局最优（离线评估极快，开销可忽略）。</summary>
        private AnnealResult AnnealMultiStart(SearchContext ctx, List<Slot> smartStart, List<Slot> original,
            System.Random rng, int iterations, int restarts, float temp0)
        {
            var globalBest = CloneSlots(original);
            double globalScore = EvaluateLayout(ctx, globalBest);

            var starts = new List<List<Slot>> { smartStart };
            if (plugin.EnableRandomStarts.Value)
            {
                starts.Add(original);
                var r1 = CloneSlots(original);
                Scramble(r1, rng);
                starts.Add(r1);
                var r2 = CloneSlots(original);
                Scramble(r2, rng);
                starts.Add(r2);
            }

            foreach (List<Slot> start in starts)
            {
                var res = Anneal(ctx, start, rng, iterations, restarts, temp0);
                if (res.Score > globalScore)
                {
                    globalScore = res.Score;
                    globalBest = res.Best;
                }
            }

            return new AnnealResult { Best = globalBest, Score = globalScore };
        }

        // ---------------------------------------------------------------- 邻域操作（含定向移动）

        private void Mutate(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            var itemIdx = new List<int>();
            var emptyIdx = new List<int>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].hasItem)
                {
                    itemIdx.Add(i);
                }
                else
                {
                    emptyIdx.Add(i);
                }
            }

            if (itemIdx.Count == 0)
            {
                return;
            }

            int roll = rng.Next(100);

            // 定向移动族（合计约 45%）
            if (roll < 12 && plugin.CriteriaMoveChance.Value > 0f)
            {
                if (TryCriteriaMove(ctx, slots, rng)) return;
            }
            if (roll < 22 && plugin.PlanetBonus.Value > 0f)
            {
                if (TryPlanetMove(ctx, slots, rng)) return;
            }
            if (roll < 32 && plugin.CompassBonus.Value > 0f)
            {
                if (TryCompassMove(ctx, slots, rng)) return;
            }
            if (roll < 37 && ctx.burdens.Count > 0)
            {
                if (TryBurdenDump(ctx, slots, rng)) return;
            }
            if (roll < 45 && plugin.HourglassBonus.Value > 0f)
            {
                if (TryHourglassMove(ctx, slots, rng)) return;
            }

            // 随机移动/交换/旋转
            if (roll < 67 && emptyIdx.Count > 0)
            {
                int a = itemIdx[rng.Next(itemIdx.Count)];
                int b = emptyIdx[rng.Next(emptyIdx.Count)];
                SwapSlots(slots, a, b);
                return;
            }

            if (roll < 87)
            {
                if (itemIdx.Count >= 2)
                {
                    int a = itemIdx[rng.Next(itemIdx.Count)];
                    int b = itemIdx[rng.Next(itemIdx.Count)];
                    if (a != b) SwapSlots(slots, a, b);
                }
                return;
            }

            for (int tries = 0; tries < 8; tries++)
            {
                int a = itemIdx[rng.Next(itemIdx.Count)];
                Slot slot = slots[a];
                if (slot.tablet != null &&
                    DungeonManager.IsTabletRotatable(slot.tablet.instanceID, slot.tablet.isRotatable))
                {
                    slot.rotation = (slot.rotation + 1 + rng.Next(3)) % 4;
                    return;
                }
            }

            if (itemIdx.Count >= 2)
            {
                int a = itemIdx[rng.Next(itemIdx.Count)];
                int b = itemIdx[rng.Next(itemIdx.Count)];
                if (a != b) SwapSlots(slots, a, b);
            }
        }

        private bool TryCriteriaMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            var candidates = new List<int>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].hasItem && slots[i].charm != null && KindPriority(GetPositionKind(slots[i].charm)) >= 2)
                {
                    candidates.Add(i);
                }
            }
            if (candidates.Count == 0)
            {
                return false;
            }

            int from = candidates[rng.Next(candidates.Count)];
            CharmPositionKind kind = GetPositionKind(slots[from].charm);
            bool preferIgnore = ctx.itemByInstance.TryGetValue(slots[from].instanceID, out ItemInfo fromInfo) &&
                                fromInfo != null && fromInfo.preferIgnoreCells;

            var targets = new List<int>();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                if (cell == from)
                {
                    continue;
                }
                int x = cell % ctx.width;
                int y = cell / ctx.width;
                bool isIgnore = ctx.ignore[cell];
                bool natural = IsSatisfyingCell(ctx.inv, kind, x, y, cell, ctx.storage, ctx.width);
                bool sat = isIgnore || natural;
                if (sat && !(slots[cell].hasItem && slots[cell].charm != null &&
                             KindPriority(GetPositionKind(slots[cell].charm)) >= 2))
                {
                    // 冰锁类：只去豁免格（有豁免格目标时）；普通受限：只去自然满足格
                    if (preferIgnore && !isIgnore)
                    {
                        continue;
                    }
                    if (!preferIgnore && !natural)
                    {
                        continue;
                    }
                    targets.Add(cell);
                }
            }
            // 冰锁类没有可用豁免格时，回退到自然满足格
            if (targets.Count == 0 && preferIgnore)
            {
                for (int cell = 0; cell < ctx.storage; cell++)
                {
                    if (cell == from)
                    {
                        continue;
                    }
                    int x = cell % ctx.width;
                    int y = cell / ctx.width;
                    if (IsSatisfyingCell(ctx.inv, kind, x, y, cell, ctx.storage, ctx.width) &&
                        !(slots[cell].hasItem && slots[cell].charm != null &&
                          KindPriority(GetPositionKind(slots[cell].charm)) >= 2))
                    {
                        targets.Add(cell);
                    }
                }
            }
            if (targets.Count == 0)
            {
                return false;
            }
            SwapSlots(slots, from, targets[rng.Next(targets.Count)]);
            return true;
        }

        /// <summary>行星聚簇：把 PLANET 藏品移到行星望远镜相邻空格（或交换）。</summary>
        private bool TryPlanetMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            // 找一个望远镜
            int moduleIdx = -1;
            foreach (ItemInfo it in ctx.charms)
            {
                if (it.isPlanetModule && slots[it.index] != null && slots[it.index].hasItem)
                {
                    moduleIdx = it.index;
                    break;
                }
            }
            if (moduleIdx < 0)
            {
                return false;
            }

            // 找一个不在望远镜身边的 PLANET 藏品
            int mx = moduleIdx % ctx.width;
            int my = moduleIdx / ctx.width;
            var planets = new List<int>();
            foreach (ItemInfo it in ctx.items)
            {
                if (it.isPlanetCategory && !it.excludeFromPlanetCluster &&
                    it.index != moduleIdx && slots[it.index] != null && slots[it.index].hasItem)
                {
                    int px = it.index % ctx.width;
                    int py = it.index / ctx.width;
                    bool adjacent = Math.Abs(px - mx) <= 1 && Math.Abs(py - my) <= 1;
                    if (!adjacent)
                    {
                        planets.Add(it.index);
                    }
                }
            }
            if (planets.Count == 0)
            {
                return false;
            }

            // 找一个望远镜身边的空格（或可交换格）
            var targets = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                int nx = mx + Neighbor8[i].x;
                int ny = my + Neighbor8[i].y;
                if (InBounds(nx, ny, ctx.width, ctx.height))
                {
                    int idx = ny * ctx.width + nx;
                    if (idx >= 0 && idx < ctx.storage &&
                        !(slots[idx] != null && slots[idx].hasItem))
                    {
                        targets.Add(idx);
                    }
                }
            }
            if (targets.Count == 0)
            {
                return false;
            }

            SwapSlots(slots, planets[rng.Next(planets.Count)], targets[rng.Next(targets.Count)]);
            return true;
        }

        /// <summary>罗盘配对：把指北针移到伤害类藏品/指北针的正下方，或把伤害藏品移到指北针上方。</summary>
        private bool TryCompassMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            var compasses = new List<int>();
            foreach (ItemInfo it in ctx.charms)
            {
                if (it.isCompass && slots[it.index] != null && slots[it.index].hasItem)
                {
                    compasses.Add(it.index);
                }
            }
            if (compasses.Count == 0)
            {
                return false;
            }

            // 目标：把某块指北针移到"上方有有效依赖"的格子（空格或可交换格），或把攻击类藏品移到指北针上方
            var compassTargets = new List<int>();
            var damageTargets = new List<int>();
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                int x = cell % ctx.width;
                int y = cell / ctx.width;
                int above = (y - 1) * ctx.width + x;
                if (y > 0 && above >= 0 && above < ctx.storage && slots[above] != null && slots[above].hasItem && slots[above].charm != null)
                {
                    bool valid = slots[above].charm is Charm_UpCharmDamage ||
                                 (slots[above].charm is IAttackableCharm ac && ac.IsAttackableCharm());
                    if (valid)
                    {
                        // 目标格：空格，或任意非指北针物品（允许交换腾位；评分会拒绝不划算的交换）
                        if (!(slots[cell] != null && slots[cell].hasItem && slots[cell].charm is Charm_UpCharmDamage))
                        {
                            compassTargets.Add(cell);
                        }
                    }
                }
                // 反向：把攻击类藏品放到指北针上方（目标格为空或可交换）
                int below = (y + 1) * ctx.width + x;
                if (y < ctx.height - 1 && below < ctx.storage && slots[below] != null && slots[below].hasItem && slots[below].charm is Charm_UpCharmDamage)
                {
                    if (!(slots[cell] != null && slots[cell].hasItem))
                    {
                        damageTargets.Add(cell);
                    }
                }
            }

            if (compassTargets.Count > 0 && rng.Next(2) == 0)
            {
                int from = compasses[rng.Next(compasses.Count)];
                SwapSlots(slots, from, compassTargets[rng.Next(compassTargets.Count)]);
                return true;
            }

            if (damageTargets.Count > 0)
            {
                var damages = new List<int>();
                foreach (ItemInfo it in ctx.charms)
                {
                    if (it.isAttackable && !it.isCompass && slots[it.index] != null && slots[it.index].hasItem)
                    {
                        damages.Add(it.index);
                    }
                }
                if (damages.Count > 0)
                {
                    SwapSlots(slots, damages[rng.Next(damages.Count)], damageTargets[rng.Next(damageTargets.Count)]);
                    return true;
                }
            }

            return false;
        }

        /// <summary>沙漏配对：把发光的沙漏移到 CD 最长的魔法书左边（或把 CD 长的魔法书移到沙漏右边）。</summary>
        private bool TryHourglassMove(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            var hourglassIdx = new List<int>();
            var magicIdx = new List<int>();
            foreach (ItemInfo it in ctx.items)
            {
                if (!(slots[it.index] != null && slots[it.index].hasItem))
                {
                    continue;
                }
                if (it.isHourglass)
                {
                    hourglassIdx.Add(it.index);
                }
                else if (it.isMagicBook)
                {
                    magicIdx.Add(it.index);
                }
            }
            if (hourglassIdx.Count == 0 || magicIdx.Count == 0)
            {
                return false;
            }

            // CD 最长的魔法书优先配对（CD 越长，沙漏放它左边收益越大）
            magicIdx.Sort((a, b) =>
            {
                float ca = ctx.itemByInstance.TryGetValue(slots[a].instanceID, out ItemInfo ia) && ia != null ? ia.magicCd : 0f;
                float cb = ctx.itemByInstance.TryGetValue(slots[b].instanceID, out ItemInfo ib) && ib != null ? ib.magicCd : 0f;
                return cb.CompareTo(ca);
            });
            int magic = magicIdx[rng.Next(Math.Min(2, magicIdx.Count))];
            int mx = magic % ctx.width;
            int my = magic / ctx.width;

            // 方式1：把某块沙漏移到这本魔法书左边格（空格或可交换格）
            if (mx > 0)
            {
                int left = my * ctx.width + (mx - 1);
                if (left >= 0 && left < ctx.storage &&
                    !(slots[left] != null && slots[left].hasItem && slots[left].charm is Charm_RightSpellCooldownHelper))
                {
                    int hg = hourglassIdx[rng.Next(hourglassIdx.Count)];
                    if (left != hg)
                    {
                        SwapSlots(slots, hg, left);
                        return true;
                    }
                }
            }

            // 方式2：把 CD 最长的魔法书移到某块沙漏右边格（空格或可交换格）
            int h = hourglassIdx[rng.Next(hourglassIdx.Count)];
            int hx = h % ctx.width;
            int hy = h / ctx.width;
            if (hx + 1 < ctx.width)
            {
                int right = hy * ctx.width + (hx + 1);
                if (right >= 0 && right < ctx.storage &&
                    !(slots[right] != null && slots[right].hasItem && slots[right].charm is Charm_Magic))
                {
                    if (right != magic)
                    {
                        SwapSlots(slots, magic, right);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>负担物品倾倒：把负面藏品移到当前最差的（负等级）格子。</summary>
        private bool TryBurdenDump(SearchContext ctx, List<Slot> slots, System.Random rng)
        {
            int worst = -1;
            int worstLevel = int.MaxValue;
            for (int cell = 0; cell < ctx.storage; cell++)
            {
                int lvl = ctx.cellLevel[cell];
                if (lvl < worstLevel)
                {
                    worstLevel = lvl;
                    worst = cell;
                }
            }
            if (worst < 0)
            {
                return false;
            }

            var burdenIdx = new List<int>();
            foreach (ItemInfo it in ctx.burdens)
            {
                if (slots[it.index] != null && slots[it.index].hasItem)
                {
                    burdenIdx.Add(it.index);
                }
            }
            if (burdenIdx.Count == 0)
            {
                return false;
            }

            int from = burdenIdx[rng.Next(burdenIdx.Count)];
            if (from != worst)
            {
                SwapSlots(slots, from, worst);
                return true;
            }
            return false;
        }

        private static void SwapSlots(List<Slot> slots, int a, int b)
        {
            Slot tmp = slots[a];
            slots[a] = slots[b];
            slots[b] = tmp;
        }

        private static List<Slot> CloneSlots(List<Slot> src)
        {
            var dst = new List<Slot>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                dst.Add(src[i].Clone());
            }
            return dst;
        }

        // ---------------------------------------------------------------- 状态捕获/应用

        private static List<Slot> CaptureState(GridInventory inv)
        {
            var slots = new List<Slot>(inv.CurrentInventoryStorage);
            for (int i = 0; i < inv.CurrentInventoryStorage; i++)
            {
                ItemPosition pos = inv.IdxToPos(i);
                if (inv.inventoryMatrix.TryGetValue(pos, out var item) && item != null)
                {
                    slots.Add(new Slot
                    {
                        hasItem = true,
                        instanceID = item.InstanceID,
                        entityID = item.EntityID,
                        quantity = item.Quantity,
                        charm = item.Charm,
                        tablet = item.StoneTablet,
                        rotation = item.StoneTablet != null ? item.StoneTablet.rotation : 0
                    });
                }
                else
                {
                    slots.Add(Slot.Empty());
                }
            }
            return slots;
        }

        private static void ApplyState(GridInventory inv, List<Slot> slots)
        {
            for (int i = 0; i < inv.CurrentInventoryStorage; i++)
            {
                ItemPosition pos = inv.IdxToPos(i);
                inv.inventoryMatrix.Remove(pos);
                inv.charms.Remove(pos);
                inv.stoneTablets.Remove(pos);
            }

            for (int j = 0; j < slots.Count && j < inv.CurrentInventoryStorage; j++)
            {
                Slot slot = slots[j];
                if (!slot.hasItem)
                {
                    continue;
                }

                ItemPosition pos = inv.IdxToPos(j);
                inv.inventoryMatrix[pos] = new NewItemOwnInstance(
                    slot.instanceID, slot.entityID, pos.x, pos.y, slot.quantity, slot.charm, slot.tablet);

                if (slot.charm != null)
                {
                    inv.charms[pos] = slot.charm;
                    slot.charm.NetworkxIdx = pos.x;
                    slot.charm.NetworkyIdx = pos.y;
                }

                if (slot.tablet != null)
                {
                    inv.stoneTablets[pos] = slot.tablet;
                    slot.tablet.NetworkxIdx = pos.x;
                    slot.tablet.NetworkyIdx = pos.y;
                    slot.tablet.Networkrotation = slot.rotation;
                }
            }
        }

        private static float ApplyAndScore(GridInventory inv, List<Slot> slots)
        {
            using (new GridInventory.Permission(inv))
            {
                ApplyState(inv, slots);
            }
            return SafeScore(inv);
        }

        private static float SafeScore(GridInventory inv)
        {
            if (!NetworkServer.active)
            {
                return float.NaN;
            }
            return inv.EvaluateCurrentAutoArrangeScore();
        }

        // ---------------------------------------------------------------- 自检

        private void SortSelfTest(GridInventory inv)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var original = CaptureState(inv);
            if (!VerifyInventorySnapshot(inv, original))
            {
                Notify("背包状态未就绪，本次未整理（请稍后再试）。");
                return;
            }
            SearchContext ctx = BuildContext(inv, original);
            double before = EvaluateLayout(ctx, original);
            float beforeGame = SafeScore(inv);

            LogItemIdentification(ctx);

            var rng = new System.Random(Environment.TickCount);
            var scrambled = CloneSlots(original);
            Scramble(scrambled, rng);
            double scrambledScore = EvaluateLayout(ctx, scrambled);
            Plugin.Log.LogInfo($"自检：已随机打乱（离线 {before:F0} -> {scrambledScore:F0}）。");

            double smartScore = scrambledScore;
            var start = scrambled;
            if (plugin.EnableSmartStart.Value)
            {
                start = BuildSmartStart(ctx);
                smartScore = EvaluateLayout(ctx, start);
                Plugin.Log.LogInfo($"自检：智能初始布局（离线 {scrambledScore:F0} -> {smartScore:F0}）。");
            }

            var result = AnnealMultiStart(ctx, start, original, rng,
                plugin.EnhancedIterations.Value, plugin.EnhancedRestarts.Value,
                plugin.EnhancedTemperature.Value);

            List<Slot> finalLayout = result.Score >= before - 0.5 ? result.Best : original;
            float finalGame = ApplyAndScore(inv, finalLayout);
            double finalOffline = EvaluateLayout(ctx, finalLayout);

            LogLayoutGrid(ctx, finalLayout, "自检");
            LogLayoutAnalysis(ctx, finalLayout, "自检");

            sw.Stop();
            Plugin.Log.LogInfo(
                $"自检完成（{sw.ElapsedMilliseconds}ms）：离线 {before:F0} → 乱序 {scrambledScore:F0} → " +
                $"智能初始 {smartScore:F0} → 整理后 {finalOffline:F0}（搜索最优 {result.Score:F0}）；" +
                $"游戏评分 {beforeGame:F0} -> {finalGame:F0}；布局 {ctx.items.Count} 件" +
                $"（石板{ctx.steles.Count} 护符{ctx.charms.Count} 负担{ctx.burdens.Count}）");
            Notify($"自检：离线 {before:F0} → 整理后 {finalOffline:F0}（{sw.ElapsedMilliseconds}ms）");
        }

        private static void Scramble(List<Slot> slots, System.Random rng)
        {
            var occupied = new List<int>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].hasItem)
                {
                    occupied.Add(i);
                }
            }

            for (int i = occupied.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                if (occupied[i] != occupied[j])
                {
                    SwapSlots(slots, occupied[i], occupied[j]);
                }
            }

            foreach (int idx in occupied)
            {
                Slot slot = slots[idx];
                if (slot.tablet != null && rng.Next(2) == 0 &&
                    DungeonManager.IsTabletRotatable(slot.tablet.instanceID, slot.tablet.isRotatable))
                {
                    slot.rotation = rng.Next(4);
                }
            }
        }

        // ---------------------------------------------------------------- 通知

        private void Notify(string msg)
        {
            Plugin.Log.LogInfo(msg);
            if (!plugin.ShowNotifications.Value)
            {
                return;
            }

            try
            {
                if (UIManager.Instance != null)
                {
                    UIManager.Instance.GetElement<UI_SystemMessage>()?.Open(msg, 2.5f);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"游戏内提示显示失败: {ex.Message}");
            }
        }
    }
}
