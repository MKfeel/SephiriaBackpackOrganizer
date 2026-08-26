using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SephiriaBackpackOrganizer
{
    /// <summary>
    /// 当前游戏会话的手动提权顺序。列表从旧到新保存；显示排名时最后一项为 P1。
    /// 只在点击、会话切换和整理快照构建时更新，不做每帧配置/背包扫描。
    /// </summary>
    internal static class ManualPriorityManager
    {
        private static readonly List<int> OrderedInstanceIds = new List<int>();
        internal static int Count => OrderedInstanceIds.Count;

        internal static int Toggle(int instanceId)
        {
            int existing = OrderedInstanceIds.IndexOf(instanceId);
            if (existing >= 0)
            {
                OrderedInstanceIds.RemoveAt(existing);
                return 0;
            }

            OrderedInstanceIds.Add(instanceId);
            return 1;
        }

        internal static int GetRank(int instanceId)
        {
            int index = OrderedInstanceIds.IndexOf(instanceId);
            return index < 0 ? 0 : OrderedInstanceIds.Count - index;
        }

        internal static Dictionary<int, int> PruneAndSnapshot(HashSet<int> presentCharmIds)
        {
            for (int i = OrderedInstanceIds.Count - 1; i >= 0; i--)
            {
                if (presentCharmIds == null || !presentCharmIds.Contains(OrderedInstanceIds[i]))
                {
                    OrderedInstanceIds.RemoveAt(i);
                }
            }

            var result = new Dictionary<int, int>(OrderedInstanceIds.Count);
            for (int i = 0; i < OrderedInstanceIds.Count; i++)
            {
                result[OrderedInstanceIds[i]] = OrderedInstanceIds.Count - i;
            }
            return result;
        }

        internal static void Clear()
        {
            if (OrderedInstanceIds.Count == 0)
            {
                return;
            }
            OrderedInstanceIds.Clear();
            RefreshVisibleBadges();
        }

        internal static int RefreshVisibleBadges()
        {
            int shown = 0;
            UI_NewInventoryIcon[] icons = Resources.FindObjectsOfTypeAll<UI_NewInventoryIcon>();
            foreach (UI_NewInventoryIcon icon in icons)
            {
                if (icon != null && icon.gameObject.scene.IsValid())
                {
                    if (ManualPriorityBadge.GetOrCreate(icon).Refresh())
                    {
                        shown++;
                    }
                }
            }
            return shown;
        }
    }

    /// <summary>图标左上角的分辨率无关 P1/P2 标记；尺寸使用图标 RectTransform 的比例锚点。</summary>
    internal sealed class ManualPriorityBadge : MonoBehaviour
    {
        private UI_NewInventoryIcon owner;
        private GameObject badgeRoot;
        private TextMeshProUGUI label;

        internal static ManualPriorityBadge GetOrCreate(UI_NewInventoryIcon icon)
        {
            ManualPriorityBadge badge = icon.GetComponent<ManualPriorityBadge>();
            if (badge == null)
            {
                badge = icon.gameObject.AddComponent<ManualPriorityBadge>();
            }
            badge.owner = icon;
            badge.EnsureVisual();
            return badge;
        }

        private void EnsureVisual()
        {
            if (badgeRoot != null || owner == null)
            {
                return;
            }

            badgeRoot = new GameObject("ManualPriorityBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            badgeRoot.transform.SetParent(owner.transform, false);
            badgeRoot.transform.SetAsLastSibling();

            RectTransform rect = (RectTransform)badgeRoot.transform;
            rect.anchorMin = new Vector2(0.03f, 0.02f);
            rect.anchorMax = new Vector2(0.38f, 0.23f);
            rect.pivot = new Vector2(0f, 0f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            label = badgeRoot.GetComponent<TextMeshProUGUI>();
            if (owner.quantityText != null)
            {
                label.font = owner.quantityText.font;
            }
            label.text = "P1";
            label.color = new Color(1f, 0.82f, 0.20f, 1f);
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.enableAutoSizing = true;
            label.fontSizeMin = 4f;
            label.fontSizeMax = owner.quantityText != null ? Math.Max(7f, owner.quantityText.fontSize * 0.58f) : 11f;
            label.raycastTarget = false;
            badgeRoot.SetActive(false);
        }

        internal bool Refresh()
        {
            EnsureVisual();
            if (badgeRoot == null || owner == null)
            {
                return false;
            }

            Plugin plugin = Plugin.Instance;
            NewItemOwnInstance item = owner.Item;
            int rank = item != null ? ManualPriorityManager.GetRank(item.InstanceID) : 0;
            bool show = plugin != null && plugin.ManualPriorityEnabled.Value &&
                        plugin.ShowManualPriorityBadge.Value &&
                        item != null && item.Charm != null && rank > 0;
            badgeRoot.SetActive(show);
            if (show)
            {
                label.text = "P" + rank;
                badgeRoot.transform.SetAsLastSibling();
            }
            return show;
        }
    }

    [HarmonyPatch(typeof(UI_NewInventoryIcon), nameof(UI_NewInventoryIcon.OnPointerClick))]
    internal static class ManualPriorityClickPatch
    {
        private static bool Prefix(UI_NewInventoryIcon __instance, PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Middle)
            {
                return true;
            }

            Plugin plugin = Plugin.Instance;
            if (plugin == null || !plugin.ManualPriorityEnabled.Value)
            {
                return true;
            }

            NewItemOwnInstance item = __instance != null ? __instance.Item : null;
            GridInventory inventory = __instance != null ? __instance.Inventory : null;
            if (__instance == null || !__instance.Showing || item == null || item.Charm == null ||
                inventory == null || !inventory.isLocalPlayer)
            {
                return true;
            }

            if (plugin.IsSorting)
            {
                Plugin.Log.LogInfo("整理进行中，已忽略本次中键提权操作。");
                return false;
            }

            bool selected = ManualPriorityManager.Toggle(item.InstanceID) != 0;
            int shown = ManualPriorityManager.RefreshVisibleBadges();
            int rank = ManualPriorityManager.GetRank(item.InstanceID);
            Plugin.Log.LogInfo(selected
                ? $"神器手动提权：instance={item.InstanceID}，当前 P{rank}；已提权 {ManualPriorityManager.Count} 件，界面标记 {shown} 个"
                : $"已取消神器手动提权：instance={item.InstanceID}；剩余 {ManualPriorityManager.Count} 件，界面标记 {shown} 个");
            return false;
        }
    }

    [HarmonyPatch(typeof(UI_NewInventoryIcon), nameof(UI_NewInventoryIcon.SetItemReference))]
    internal static class ManualPrioritySetItemPatch
    {
        private static void Postfix(UI_NewInventoryIcon __instance)
        {
            ManualPriorityBadge.GetOrCreate(__instance).Refresh();
        }
    }

    [HarmonyPatch(typeof(UI_NewInventoryIcon), nameof(UI_NewInventoryIcon.UpdateIcon))]
    internal static class ManualPriorityUpdateIconPatch
    {
        private static void Postfix(UI_NewInventoryIcon __instance)
        {
            ManualPriorityBadge.GetOrCreate(__instance).Refresh();
        }
    }

    [HarmonyPatch(typeof(UI_NewInventoryIcon), "OnEnable")]
    internal static class ManualPriorityEnablePatch
    {
        private static void Postfix(UI_NewInventoryIcon __instance)
        {
            ManualPriorityBadge.GetOrCreate(__instance).Refresh();
        }
    }
}
