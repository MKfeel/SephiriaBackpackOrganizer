# -*- coding: utf-8 -*-
"""生成 Sephiria 背包整理插件介绍文档所用的示意图。"""
import os
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch
import numpy as np

plt.rcParams['font.sans-serif'] = ['Microsoft YaHei', 'SimHei', 'SimSun']
plt.rcParams['axes.unicode_minus'] = False

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'figures')
os.makedirs(OUT, exist_ok=True)

C_BLUE = '#2f6fad'
C_GREEN = '#3d8b4f'
C_ORANGE = '#c47a1f'
C_RED = '#b4433c'
C_GRAY = '#6b7280'
C_LIGHT = '#f2f6fa'


# ---------------------------------------------------------------- 图1：整理总流程
def fig_overview():
    steps = [
        ('按 F8 触发', 'Update 轮询热键', C_BLUE),
        ('会话校验', '进入会话后等 3 秒\n快照一致性校验', C_ORANGE),
        ('读取快照+识别', '石板/护符/负担分类\n特殊机制识别', C_BLUE),
        ('智能初始布局', '贪心构造一个\n不错起点', C_GREEN),
        ('多轮模拟退火', '4 轮×多起点\n取全局最优', C_GREEN),
        ('应用最优布局', '主机：整包写回\n客户端：交换/旋转序列', C_ORANGE),
        ('提示整理完毕', '日志输出评分\n与落地情况', C_GRAY),
    ]
    fig, ax = plt.subplots(figsize=(13.5, 3.1))
    ax.set_xlim(0, 100)
    ax.set_ylim(0, 10)
    ax.axis('off')
    n = len(steps)
    bw = 13.2
    gap = (100 - n * bw) / (n - 1)
    for i, (title, sub, color) in enumerate(steps):
        x0 = i * (bw + gap) + 0.5
        box = FancyBboxPatch((x0, 1.2), bw, 7.6, boxstyle='round,pad=0.35',
                             linewidth=1.4, edgecolor=color, facecolor=C_LIGHT)
        ax.add_patch(box)
        ax.text(x0 + bw / 2, 5.9, title, ha='center', va='center', fontsize=12.5, weight='bold', color=color)
        ax.text(x0 + bw / 2, 3.4, sub, ha='center', va='center', fontsize=9, color='#333333', linespacing=1.5)
        if i < n - 1:
            ax.annotate('', xy=(x0 + bw + gap - 0.3, 5.0), xytext=(x0 + bw + 0.5, 5.0),
                        arrowprops=dict(arrowstyle='-|>', color='#555555', lw=1.6))
    ax.text(50, 9.3, '单次按 F8 的完整处理流程（全程离线计算，游戏内只应用一次）',
            ha='center', fontsize=11, color='#222222')
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, 'fig_overview.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


# ---------------------------------------------------------------- 图2：背包布局示例
def fig_backpack():
    W, H = 6, 6
    # (x,y) -> (缩写, 等级, 类型)
    cells = {}
    # 石板 T 在 (0,0)，效果覆盖 (0,0)(1,0)(0,1)(1,1)
    cells[(0, 0)] = ('T 石板', '', 'stele')
    cells[(1, 0)] = ('效', '+2', 'stelefx')
    cells[(0, 1)] = ('效', '+1', 'stelefx')
    cells[(1, 1)] = ('L 冰锁', '5', 'lock')
    cells[(2, 0)] = ('O', '', 'other')
    cells[(3, 0)] = ('', '', 'empty')
    cells[(4, 0)] = ('A 护符', '12', 'high')
    cells[(5, 0)] = ('H 晶', '8', 'harmony')
    cells[(2, 1)] = ('P 行星', '6', 'planet')
    cells[(3, 1)] = ('', '', 'empty')
    cells[(4, 1)] = ('A 护符', '13', 'high')
    cells[(5, 1)] = ('A 护符', '9', 'high')
    cells[(0, 2)] = ('', '', 'empty')
    cells[(1, 2)] = ('', '', 'empty')
    cells[(2, 2)] = ('', '', 'empty')
    cells[(3, 2)] = ('M 镜', '7', 'module')
    cells[(4, 2)] = ('P 行星', '5', 'planet')
    cells[(5, 2)] = ('', '', 'empty')
    cells[(0, 3)] = ('', '', 'empty')
    cells[(1, 3)] = ('', '', 'mystic')
    cells[(2, 3)] = ('P 行星', '8', 'planet')
    cells[(3, 3)] = ('', '', 'empty')
    cells[(4, 3)] = ('', '', 'empty')
    cells[(5, 3)] = ('K 伤', '11', 'attack')
    cells[(0, 4)] = ('', '', 'empty')
    cells[(1, 4)] = ('A 护符', '15', 'mystic')
    cells[(2, 4)] = ('', '', 'empty')
    cells[(3, 4)] = ('', '', 'empty')
    cells[(4, 4)] = ('', '', 'empty')
    cells[(5, 4)] = ('C 针', '6', 'compass')
    cells[(0, 5)] = ('D 徽章', '9', 'badge')
    cells[(1, 5)] = ('S 伴', '7', 'companion')
    cells[(2, 5)] = ('S 伴', '8', 'companion')
    cells[(3, 5)] = ('B 负担', '-4', 'burden')
    cells[(4, 5)] = ('O', '', 'other')
    cells[(5, 5)] = ('O', '', 'other')

    fig, ax = plt.subplots(figsize=(10.2, 8.8))
    ax.set_xlim(-2.4, 7.4)
    ax.set_ylim(-1.9, 7.1)
    ax.axis('off')

    face = {'stele': '#f7ecd4', 'stelefx': '#fbf5e4', 'lock': '#e7f3e7', 'planet': '#e2ecf7',
            'high': '#f0eaf6', 'harmony': '#fdeee8', 'module': '#dcefe0', 'mystic': '#fdf1dc',
            'attack': '#f5e3e0', 'compass': '#e8f0f8', 'badge': '#fbe4ec', 'companion': '#fdeef2',
            'burden': '#f7dcdb', 'other': '#f0f1f3', 'empty': 'white'}
    edge = {'stele': '#8a6d3b', 'stelefx': '#c9b98a', 'lock': '#2e7d32', 'planet': '#2f6fad',
            'high': '#7a5ca8', 'harmony': '#d96c3f', 'module': '#2e7d32', 'mystic': '#c47a1f',
            'attack': '#a94a42', 'compass': '#2f6fad', 'badge': '#c2456e', 'companion': '#d98aa5',
            'burden': '#a94442', 'other': '#9aa0a6', 'empty': '#cfd4d9'}

    for y in range(H):
        for x in range(W):
            txt, lvl, kind = cells[(x, y)]
            rect = plt.Rectangle((x, y), 1, 1, facecolor=face[kind], edgecolor=edge[kind], lw=1.6, zorder=2)
            ax.add_patch(rect)
            if txt:
                ax.text(x + 0.5, y + 0.66, txt, ha='center', va='center', fontsize=10.5, weight='bold', zorder=3)
            if lvl:
                color = C_RED if lvl.startswith('-') else '#444444'
                ax.text(x + 0.5, y + 0.24, '等级 ' + lvl, ha='center', va='center', fontsize=7.6, color=color, zorder=3)
            if kind == 'mystic':
                ax.text(x + 0.92, y + 0.92, '×2', ha='center', va='center', fontsize=7.5, color=C_ORANGE, weight='bold', zorder=3)
            if kind == 'lock':
                ax.text(x + 0.5, y + 0.9, '豁免格', ha='center', va='center', fontsize=6.2, color='#2e7d32', zorder=3)

    # 关联标注（带箭头的文字）
    def link(x1, y1, x2, y2, color, label, lx, ly):
        ax.annotate('', xy=(x2 + 0.5, y2 + 0.5), xytext=(x1 + 0.5, y1 + 0.5),
                    arrowprops=dict(arrowstyle='-|>', color=color, lw=1.4,
                                    connectionstyle='arc3,rad=0.25', linestyle='--'), zorder=4)
        ax.text(lx, ly, label, fontsize=8.4, color=color, zorder=5)

    link(3, 2, 2, 1, '#2f6fad', '行星聚簇\n+40000/颗', 1.95, 1.9)
    link(3, 2, 4, 2, '#2f6fad', '', 4.35, 1.55)
    link(3, 2, 2, 3, '#2f6fad', '', 2.55, 3.35)
    link(5, 0, 4, 0, '#d96c3f', '和谐之晶：周围8格\n等级和 ×2000', 3.35, -0.62)
    link(0, 5, 1, 5, '#c2456e', '奉献徽章：同行同伴\n+3000/个', 3.6, 6.05)
    link(0, 5, 2, 5, '#c2456e', '', 4.3, 6.35)
    link(5, 4, 5, 3, '#7a5ca8', '指北针上方是\n伤害类才生效', 6.15, 3.05)
    link(1, 4, 1, 3, C_ORANGE, '神秘地块\n等级 ×2', -1.35, 3.15)

    # 图例
    legend_items = [
        ('T 石板（覆盖2×2，含效果网格）', '#f7ecd4', '#8a6d3b'),
        ('L 冰锁（站豁免格，解除位置限制）', '#e7f3e7', '#2e7d32'),
        ('M 行星望远镜（周围8格聚行星）', '#dcefe0', '#2e7d32'),
        ('P 行星 / K 伤害类藏品', '#e2ecf7', '#2f6fad'),
        ('H 和谐之晶（周围8格等级和放大）', '#fdeee8', '#d96c3f'),
        ('D 奉献徽章 / S 同伴（同横排强化）', '#fbe4ec', '#c2456e'),
        ('C 指北针（上方配对生效）', '#e8f0f8', '#2f6fad'),
        ('B 负担（塞进负等级最差格）', '#f7dcdb', '#a94442'),
        ('★ 神秘 ×2 地块（放高价值护符）', '#fdf1dc', '#c47a1f'),
        ('A 高等级护符 / O 杂物 / 空格', '#f0f1f3', '#9aa0a6'),
    ]
    lx, ly = 0.1, -1.45
    for i, (label, fc, ec) in enumerate(legend_items):
        row, col = divmod(i, 2)
        bx = lx + col * 3.35
        by = ly - row * 0.52
        rect = plt.Rectangle((bx, by), 0.34, 0.34, facecolor=fc, edgecolor=ec, lw=1.2)
        ax.add_patch(rect)
        ax.text(bx + 0.45, by + 0.17, label, fontsize=8, va='center', color='#333333')

    ax.text(3.15, 6.28, '一张布局能同时吃到的机制（示意图，格子大小与等级均为示意）',
            ha='center', fontsize=10.5, color='#222222')
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, 'fig_backpack.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


# ---------------------------------------------------------------- 图3：评分权重
def fig_weights():
    items = [
        ('行锁定跨行惩罚', -100000, '每件每次', C_RED),
        ('负担惩罚', -20000, '每高1级', C_RED),
        ('行星聚簇', 40000, '每颗行星', C_GREEN),
        ('罗盘配对', 12000, '每个配对', C_GREEN),
        ('护符等级分', 10000, '每有效等级', C_BLUE),
        ('奉献徽章同行', 3000, '每个同伴', C_GREEN),
        ('和谐之晶', 2000, '周围每级', C_GREEN),
        ('启用奖励', 1000, '每件', C_BLUE),
        ('负等级暴露', -250, '每级', C_RED),
    ]
    items = sorted(items, key=lambda t: t[1])
    labels = [t[0] for t in items]
    vals = [t[1] for t in items]
    colors = [t[3] for t in items]
    notes = [t[2] for t in items]

    fig, ax = plt.subplots(figsize=(11, 5.6))
    y = np.arange(len(items))
    bars = ax.barh(y, vals, color=colors, alpha=0.88, height=0.62)
    ax.set_yticks(y)
    ax.set_yticklabels(labels, fontsize=10.5)
    ax.axvline(0, color='#888888', lw=1)
    for yi, v, note in zip(y, vals, notes):
        off = 900 if v >= 0 else -900
        ha = 'left' if v >= 0 else 'right'
        sign = '+' if v > 0 else ''
        ax.text(v + off, yi, f'{sign}{v:,}（{note}）', va='center', ha=ha, fontsize=9, color='#333333')
    ax.set_xlim(-130000, 130000)
    ax.set_xlabel('评分增量（默认配置，正值加分/负值扣分）', fontsize=10)
    ax.set_title('评分模型各项权重（离线目标函数，搜索按它找最优布局）', fontsize=12.5, pad=12)
    ax.grid(axis='x', ls=':', alpha=0.4)
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, 'fig_weights.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


# ---------------------------------------------------------------- 图4：智能初始布局顺序
def fig_smartstart():
    groups = [
        ('1 石板贪心摆位', '负效果石板优先，逐格逐旋转打分\n（正覆盖最大化、豁免/负等级格预判）', C_BLUE),
        ('2 受限护符', '有位置条件的先放：\n满足条件的格 > 豁免格 > 任意格', C_GREEN),
        ('3 行锁定物品', '凯尔萨德尼钥匙等：\n保持用户所在行，行内选最佳列', C_GREEN),
        ('4 行星望远镜', '先落位，为行星聚簇定锚点', C_BLUE),
        ('5 和谐之晶', '先落位，收集其 8 邻域格\n（+8000 加权吸引高等级护符）', C_ORANGE),
        ('6 奉献徽章', '先落位并记录所在行\n同行格 +6000 引导同伴同排', C_ORANGE),
        ('7 行星聚簇', '行星放望远镜相邻格\n（排除配置列表内的行星类藏品）', C_GREEN),
        ('8 罗盘配对', '优先放在"上方是伤害类"\n的格子', C_GREEN),
        ('9 其余护符', '按用户优先级 P1→P4\n同优先级内按稀有度', C_BLUE),
        ('10 杂物填空', '普通物品填剩余空格', C_GRAY),
        ('11 负担塞负格', '负面藏品塞进最差\n（负等级最高）的格子', C_RED),
    ]
    fig, ax = plt.subplots(figsize=(10.2, 7.6))
    ax.set_xlim(0, 10)
    ax.set_ylim(0, 11)
    ax.axis('off')
    for i, (title, sub, color) in enumerate(groups):
        y = 10.4 - i
        box = FancyBboxPatch((0.35, y - 0.42), 9.3, 0.84, boxstyle='round,pad=0.06',
                             linewidth=1.3, edgecolor=color, facecolor=C_LIGHT)
        ax.add_patch(box)
        ax.text(0.7, y, title, fontsize=11, weight='bold', color=color, va='center')
        ax.text(9.7, y, sub, fontsize=8.8, color='#444444', va='center', ha='right')
        if i < len(groups) - 1:
            ax.annotate('', xy=(5, y - 0.5), xytext=(5, y - 0.85),
                        arrowprops=dict(arrowstyle='-|>', color='#999999', lw=1.2))
    ax.text(5, 10.95, '智能初始布局：不靠运气，按机制优先级逐个落位', ha='center',
            fontsize=12, color='#222222')
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, 'fig_smartstart.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


# ---------------------------------------------------------------- 图5：模拟退火
def fig_anneal():
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(12.6, 4.6))

    iters = 3000
    t0 = 800.0
    i = np.arange(iters)
    T = np.maximum(1.0, t0 * (1.0 - i / iters))
    ax1.plot(i, T, color=C_BLUE, lw=2)
    ax1.set_xlabel('迭代次数', fontsize=10)
    ax1.set_ylabel('温度 T', fontsize=10)
    ax1.set_title('温度随时间线性下降（初始 800 → 最低 1）', fontsize=11.5)
    ax1.grid(ls=':', alpha=0.4)
    ax1.axhline(1, color=C_RED, ls='--', lw=1)
    ax1.text(2600, 60, '最低温 1', color=C_RED, fontsize=9)

    for d in (-1000, -400, -100):
        acc = np.exp(np.minimum(0, d / T))
        ax2.plot(i, acc, lw=2, label=f'分数差 Δ={d}')
    ax2.set_xlabel('迭代次数', fontsize=10)
    ax2.set_ylabel('接受概率 exp(Δ/T)', fontsize=10)
    ax2.set_title('温度高时接受"变差"解，低温时只保留变好', fontsize=11.5)
    ax2.legend(fontsize=9)
    ax2.grid(ls=':', alpha=0.4)

    fig.suptitle('模拟退火：先大胆探索，再精细收敛', fontsize=13, y=0.99)
    fig.tight_layout(rect=[0, 0, 1, 0.93])
    fig.savefig(os.path.join(OUT, 'fig_anneal.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


# ---------------------------------------------------------------- 图6：变异操作构成
def fig_mutation():
    labels = ['受限护符定向跳转 12%', '行星聚拢 10%', '罗盘配对 10%', '负担塞负格 6%',
              '随机移动 24%', '随机交换 20%', '石板随机旋转 18%']
    sizes = [12, 10, 10, 6, 24, 20, 18]
    colors = ['#3d8b4f', '#4a9a5c', '#57a86d', '#64b57e',
              '#2f6fad', '#4d85bd', '#9aa0a6']
    explode = [0.06, 0.06, 0.06, 0.06, 0, 0, 0]
    fig, ax = plt.subplots(figsize=(8.6, 5.6))
    wedges, texts, autotexts = ax.pie(
        sizes, explode=explode, labels=labels, colors=colors, autopct='%d%%',
        startangle=90, counterclock=False, pctdistance=0.8,
        wedgeprops=dict(width=0.42, edgecolor='white', linewidth=1.5))
    for t in autotexts:
        t.set_fontsize(9)
    for t in texts:
        t.set_fontsize(10)
    ax.text(0, 0.12, '定向移动\n约 38%', ha='center', va='center', fontsize=12, weight='bold', color=C_GREEN)
    ax.text(0, -0.32, '随机探索\n62%', ha='center', va='center', fontsize=10, color=C_BLUE)
    ax.set_title('每次迭代的变异操作构成：定向移动 + 随机探索', fontsize=12.5, pad=14)
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, 'fig_mutation.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


# ---------------------------------------------------------------- 图7：客户端整理流程
def fig_client():
    steps = [
        ('本地离线计算', '读快照→识别→智能初始\n→多轮退火，得最优布局\n（不碰游戏状态）', C_GREEN),
        ('生成操作序列', '当前布局→目标布局\n拆成 交换(位置对)\n+ 旋转(位置×次数)', C_BLUE),
        ('逐条执行', 'inv.Swap（CmdSwap）\ninv.DoClickAction\n（Mirror 授权命令）', C_ORANGE),
        ('完成提示', '日志记录 交换/旋转\n数量与评分提升', C_GRAY),
    ]
    fig, ax = plt.subplots(figsize=(12.5, 3.2))
    ax.set_xlim(0, 100)
    ax.set_ylim(0, 10)
    ax.axis('off')
    n = len(steps)
    bw = 22
    gap = (100 - n * bw) / (n - 1)
    for i, (title, sub, color) in enumerate(steps):
        x0 = i * (bw + gap) + 0.5
        box = FancyBboxPatch((x0, 1.2), bw, 7.6, boxstyle='round,pad=0.35',
                             linewidth=1.4, edgecolor=color, facecolor=C_LIGHT)
        ax.add_patch(box)
        ax.text(x0 + bw / 2, 6.2, title, ha='center', va='center', fontsize=11.5, weight='bold', color=color)
        ax.text(x0 + bw / 2, 3.4, sub, ha='center', va='center', fontsize=8.6, color='#333333', linespacing=1.6)
        if i < n - 1:
            ax.annotate('', xy=(x0 + bw + gap - 0.4, 5.0), xytext=(x0 + bw + 0.6, 5.0),
                        arrowprops=dict(arrowstyle='-|>', color='#555555', lw=1.6))
    ax.text(50, 9.3, '联机客户端整理：服务器无权限也能用（不需要清空背包，比主机版更保守）',
            ha='center', fontsize=11, color='#222222')
    fig.tight_layout()
    fig.savefig(os.path.join(OUT, 'fig_client.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


# ---------------------------------------------------------------- 图8：等级分配（裸等级→石板打光→护符就位）
def fig_levels():
    base = [
        [2, 2, 1, 1, 2, 3],
        [2, 3, 3, 2, 1, 2],
        [1, 2, 3, 2, 1, 1],
        [0, 1, 2, 1, 0, 0],
    ]
    # 石板 T 占 (1,1)，效果网格：(1,1)+2、(2,1)+1、(1,2)+1
    stele_effects = {(1, 1): 2, (2, 1): 1, (1, 2): 1}
    placed = {
        (3, 0): 'P2',
        (1, 1): 'P1',
        (2, 1): 'P2',
        (2, 2): 'P1',
    }

    fig, axes = plt.subplots(1, 3, figsize=(14.2, 4.7))

    for idx, ax in enumerate(axes):
        ax.set_xlim(-0.4, 6.4)
        ax.set_ylim(4.5, -0.9)
        ax.set_aspect('equal')
        ax.axis('off')
        for y in range(4):
            for x in range(6):
                lvl = base[y][x]
                eff = stele_effects.get((x, y), 0)
                if idx >= 1 and eff:
                    lvl += eff
                is_stele = (x, y) == (1, 1)
                is_effect = (x, y) in stele_effects and not is_stele
                if idx == 0:
                    fc = '#f4f6f8'
                    ec = '#c7ced6'
                elif is_stele:
                    fc = '#5b4a68'
                    ec = '#3d3247'
                elif is_effect:
                    fc = '#fdf1dc'
                    ec = '#c47a1f'
                else:
                    fc = '#f4f6f8'
                    ec = '#c7ced6'
                rect = plt.Rectangle((x, y), 1, 1, facecolor=fc, edgecolor=ec, lw=1.4, zorder=2)
                ax.add_patch(rect)
                # 等级数字
                ax.text(x + 0.5, y + 0.5, str(lvl), ha='center', va='center', fontsize=12,
                        color='#333333', weight='bold', zorder=3)
                if is_stele:
                    ax.text(x + 0.5, y + 0.88, 'T 石板', ha='center', va='center', fontsize=7,
                            color='white', zorder=3)
                if is_effect:
                    ax.text(x + 0.5, y + 0.88, '+%d' % eff, ha='center', va='center', fontsize=7.5,
                            color=C_ORANGE, weight='bold', zorder=3)
                # 效果网格虚线框（在 A/B 图）
                if (x, y) in stele_effects:
                    rect2 = plt.Rectangle((x - 0.06, y - 0.06), 1.12, 1.12, fill=False,
                                          edgecolor=C_ORANGE, lw=1.1, ls='--', zorder=1)
                    ax.add_patch(rect2)
                # 护符就位（C 图）
                if idx == 2 and (x, y) in placed:
                    ax.text(x + 0.5, y + 0.32, placed[(x, y)], ha='center', va='center',
                            fontsize=11, weight='bold', color=C_BLUE, zorder=3)
                    ax.text(x + 0.5, y + 0.72, '护符', ha='center', va='center', fontsize=6.5,
                            color='#888888', zorder=3)

        titles = ['① 裸等级地形（levelMatrix）', '② 石板"打光"（效果网格 +2/+1/+1）', '③ 护符就位（P1 优先站最高等级格）']
        ax.text(3, -0.35, titles[idx], ha='center', fontsize=11.5, color='#222222')

    fig.suptitle('等级分配：石板决定哪几格亮，护符决定站哪格（同一块石板，格子等级随之变化）',
                 fontsize=12.5, y=1.0)
    fig.tight_layout(rect=[0, 0, 1, 0.93])
    fig.savefig(os.path.join(OUT, 'fig_levels.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


# ---------------------------------------------------------------- 图9：简单摆放案例（同一块石板两种摆法）
def fig_case():
    base = [
        [2, 1, 1, 2, 1, 1],
        [1, 1, 1, 2, 1, 1],
        [1, 1, 1, 1, 1, 1],
        [1, 1, 1, 1, 1, 1],
    ]
    # 石板效果网格（旋转0°）：主格+2（自身）、右邻+1、下邻+1
    # 整理前：T@(0,0)  P1@(3,3)  P2@(1,0)  P3@(0,1)
    # 整理后：T@(2,1)  P1@(3,1)  P2@(2,2)  P3@(0,0)
    layouts = [
        ('整理前', (0, 0), {(3, 3): 'P1', (1, 0): 'P2', (0, 1): 'P3'}, 65000),
        ('整理后', (2, 1), {(3, 1): 'P1', (2, 2): 'P2', (0, 0): 'P3'}, 95000),
    ]
    stele_eff = lambda sx, sy: {(sx, sy): 2, (sx + 1, sy): 1, (sx, sy + 1): 1}

    fig, (axL, axR) = plt.subplots(1, 2, figsize=(14.0, 5.0))
    axes = [axL, axR]

    for ax, (name, tpos, charms, total) in zip(axes, layouts):
        ax.set_xlim(-0.5, 6.5)
        ax.set_ylim(4.5, -0.9)
        ax.set_aspect('equal')
        ax.axis('off')
        eff = stele_eff(*tpos)
        for y in range(4):
            for x in range(6):
                lvl = base[y][x]
                is_stele = (x, y) == tpos
                is_eff = (x, y) in eff and not is_stele
                if is_eff:
                    lvl += eff[(x, y)]
                if is_stele:
                    fc, ec = '#5b4a68', '#3d3247'
                elif is_eff:
                    fc, ec = '#fdf1dc', '#c47a1f'
                elif lvl >= 2:
                    fc, ec = '#e6edf5', '#9db4c8'
                else:
                    fc, ec = '#f4f6f8', '#c7ced6'
                rect = plt.Rectangle((x, y), 1, 1, facecolor=fc, edgecolor=ec, lw=1.3, zorder=2)
                ax.add_patch(rect)
                ax.text(x + 0.5, y + 0.55, str(lvl), ha='center', va='center', fontsize=11,
                        color='#333333', weight='bold', zorder=3)
                if is_stele:
                    ax.text(x + 0.5, y + 0.88, 'T 石板', ha='center', va='center', fontsize=6.5,
                            color='white', zorder=3)
                if is_eff:
                    ax.text(x + 0.5, y + 0.88, '+%d' % eff[(x, y)], ha='center', va='center',
                            fontsize=7, color=C_ORANGE, weight='bold', zorder=3)
                if (x, y) in eff:
                    rect2 = plt.Rectangle((x - 0.06, y - 0.06), 1.12, 1.12, fill=False,
                                          edgecolor=C_ORANGE, lw=1.0, ls='--', zorder=1)
                    ax.add_patch(rect2)
                if (x, y) in charms:
                    ax.text(x + 0.5, y + 0.3, charms[(x, y)], ha='center', va='center',
                            fontsize=11, weight='bold', color=C_BLUE, zorder=3)
                    ax.text(x + 0.5, y + 0.72, '护符', ha='center', va='center', fontsize=6,
                            color='#888888', zorder=3)
        ax.text(3, -0.35, '%s：总评分 %d' % (name, total), ha='center', fontsize=12.5,
                color='#222222', weight='bold')

    # 中间对比
    fig.text(0.5, 0.5, '整理\n(移动石板+旋转)\n\n评分 +46%', ha='center', va='center',
             fontsize=12, color=C_GREEN, weight='bold', linespacing=1.6)

    fig.suptitle('简单案例：同一块石板 + 同一批护符，只是"灯光"和"站位"变了',
                 fontsize=13.5, y=1.0)
    fig.tight_layout(rect=[0, 0, 1, 0.92])
    fig.savefig(os.path.join(OUT, 'fig_case.png'), dpi=170, bbox_inches='tight')
    plt.close(fig)


if __name__ == '__main__':
    fig_overview()
    fig_backpack()
    fig_weights()
    fig_smartstart()
    fig_anneal()
    fig_mutation()
    fig_client()
    fig_levels()
    fig_case()
    print('figures done ->', OUT)
