using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ServerSyncModTemplate;

internal static class ComboChainResetPatches
{
    // Interruption policy window, independent of the game's combo continuation timeout.
    private const float ComboCarryResetWindow = 0.25f;
    private const float ForcedComboResetTime = ComboCarryResetWindow + 0.01f;
    private static readonly ConditionalWeakTable<Humanoid, ComboState> ComboStates = new();

    private sealed class ComboState
    {
        public bool WasBlocking;
        public bool WasInDodge;
        public bool WasInEmote;
        public bool BlockedDuringComboWindow;
        public bool DodgedDuringComboWindow;
        public bool EmoteInterruptedComboWindow;
        public bool LastAttackPreventsEmoteCancel;
    }

    private static ComboState GetState(Humanoid humanoid)
    {
        return ComboStates.GetOrCreateValue(humanoid);
    }

    private static bool ShouldPreventBlockComboCarry()
    {
        return ServerSyncModTemplatePlugin.PreventBlockComboCarry.Value.IsOn();
    }

    private static bool ShouldPreventEmoteCancel()
    {
        return ServerSyncModTemplatePlugin.PreventEmoteCancel.Value.IsOn();
    }

    private static bool ShouldPreventDodgeAttackQueue()
    {
        return ServerSyncModTemplatePlugin.PreventDodgeAttackQueue.Value.IsOn();
    }

    private static bool IsAffectedWeaponForBlock(ItemDrop.ItemData? weapon)
    {
        if (weapon == null)
        {
            return false;
        }

        if (weapon.m_shared.m_itemType != ItemDrop.ItemData.ItemType.OneHandedWeapon)
        {
            return false;
        }

        return weapon.m_shared.m_skillType is Skills.SkillType.Swords or Skills.SkillType.Knives or Skills.SkillType.Clubs;
    }

    private static bool IsAffectedAttackForEmote(ItemDrop.ItemData? weapon, bool secondaryAttack)
    {
        if (weapon == null)
        {
            return false;
        }

        ItemDrop.ItemData.SharedData shared = weapon.m_shared;
        if (shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon && shared.m_skillType is Skills.SkillType.Swords or Skills.SkillType.Knives or Skills.SkillType.Axes)
        {
            return true;
        }

        if (shared.m_animationState is ItemDrop.ItemData.AnimationState.DualAxes or ItemDrop.ItemData.AnimationState.TwoHandedAxe)
        {
            return true;
        }

        return shared.m_skillType == Skills.SkillType.Spears && !secondaryAttack;
    }

    private static bool IsAffectedWeaponForDodge(ItemDrop.ItemData? weapon)
    {
        if (weapon == null)
        {
            return false;
        }

        return weapon.m_shared.m_skillType is Skills.SkillType.Unarmed or Skills.SkillType.Spears or Skills.SkillType.Axes or Skills.SkillType.Polearms;
    }

    private static bool IsDodgeQueuedOrActive(Player player)
    {
        return player.m_queuedDodgeTimer > 0f || player.InDodge();
    }

    private static void ClearQueuedAttacks(Player player)
    {
        player.m_queuedAttackTimer = 0f;
        player.m_queuedSecondAttackTimer = 0f;
    }

    private static void ResetAttackChainLevels(Humanoid humanoid)
    {
        if (humanoid.m_currentAttack != null)
        {
            humanoid.m_currentAttack.m_nextAttackChainLevel = 0;
        }

        if (humanoid.m_previousAttack != null)
        {
            humanoid.m_previousAttack.m_nextAttackChainLevel = 0;
        }
    }

    private static void ClearComboResetFlags(ComboState state)
    {
        state.BlockedDuringComboWindow = false;
        state.DodgedDuringComboWindow = false;
        state.EmoteInterruptedComboWindow = false;
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
    private static class HumanoidStartAttackPatch
    {
        private static void Postfix(Humanoid __instance, bool secondaryAttack, bool __result)
        {
            if (!__result)
            {
                return;
            }

            ComboState state = GetState(__instance);
            state.LastAttackPreventsEmoteCancel = ShouldPreventEmoteCancel() && IsAffectedAttackForEmote(__instance.GetCurrentWeapon(), secondaryAttack);
        }
    }

    [HarmonyPatch(typeof(Humanoid), "UpdateBlock")]
    private static class HumanoidUpdateBlockPatch
    {
        private static void Postfix(Humanoid __instance)
        {
            bool isBlocking = __instance.IsBlocking();

            if (!ShouldPreventBlockComboCarry() || !IsAffectedWeaponForBlock(__instance.GetCurrentWeapon()))
            {
                ComboState comboState = GetState(__instance);
                comboState.WasBlocking = isBlocking;
                comboState.BlockedDuringComboWindow = false;
                return;
            }

            ComboState state = GetState(__instance);
            if (!state.WasBlocking && isBlocking && __instance.GetTimeSinceLastAttack() <= ComboCarryResetWindow)
            {
                state.BlockedDuringComboWindow = true;

                ResetAttackChainLevels(__instance);
            }

            state.WasBlocking = isBlocking;
        }
    }

    [HarmonyPatch(typeof(Player), "UpdateDodge")]
    private static class PlayerUpdateDodgePatch
    {
        private static void Postfix(Player __instance)
        {
            bool inDodge = __instance.InDodge();

            if (!ShouldPreventDodgeAttackQueue())
            {
                ComboState comboState = GetState(__instance);
                comboState.WasInDodge = inDodge;
                comboState.DodgedDuringComboWindow = false;
                return;
            }

            ComboState state = GetState(__instance);
            bool justEnteredDodge = !state.WasInDodge && inDodge;
            if (justEnteredDodge && IsAffectedWeaponForDodge(__instance.GetCurrentWeapon()))
            {
                ClearQueuedAttacks(__instance);
                state.DodgedDuringComboWindow = __instance.GetTimeSinceLastAttack() <= ComboCarryResetWindow;
                ResetAttackChainLevels(__instance);
            }

            state.WasInDodge = inDodge;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
    private static class PlayerSetControlsPatch
    {
        private static void Prefix(Player __instance, ref bool attack, ref bool attackHold, bool blockHold, bool jump, bool dodge)
        {
            if (!ShouldPreventDodgeAttackQueue())
            {
                return;
            }

            if (!IsAffectedWeaponForDodge(__instance.GetCurrentWeapon()))
            {
                return;
            }

            bool dodgeRequested = dodge || jump && blockHold;
            bool leftAndRightHeld = blockHold && (attack || attackHold);
            if (!leftAndRightHeld || !(dodgeRequested || IsDodgeQueuedOrActive(__instance)))
            {
                return;
            }

            ClearQueuedAttacks(__instance);
            attack = false;
            attackHold = false;
        }
    }

    [HarmonyPatch(typeof(Attack), nameof(Attack.Start))]
    private static class AttackStartPatch
    {
        private static void Prefix(Humanoid character, ItemDrop.ItemData weapon, ref Attack? previousAttack, ref float timeSinceLastAttack)
        {
            ComboState state = GetState(character);

            bool blockedComboCarry = ShouldPreventBlockComboCarry() && IsAffectedWeaponForBlock(weapon) && state.BlockedDuringComboWindow;
            bool dodgedComboCarry = character is Player && ShouldPreventDodgeAttackQueue() && IsAffectedWeaponForDodge(weapon) && state.DodgedDuringComboWindow;
            bool emoteComboCarry = ShouldPreventEmoteCancel() && state.EmoteInterruptedComboWindow;
            if (!blockedComboCarry && !dodgedComboCarry && !emoteComboCarry)
            {
                return;
            }

            previousAttack = null;
            timeSinceLastAttack = ForcedComboResetTime;
        }

        private static void Postfix(Humanoid character, bool __result)
        {
            if (!__result)
            {
                return;
            }

            // Failed attempts retain the interruption until an attack actually starts.
            ClearComboResetFlags(GetState(character));
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.StartEmote))]
    private static class PlayerStartEmotePatch
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            if (!ShouldPreventEmoteCancel())
            {
                return true;
            }

            ComboState state = GetState(__instance);
            if (!state.LastAttackPreventsEmoteCancel || __instance.GetTimeSinceLastAttack() > ComboCarryResetWindow)
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), "UpdateEmote")]
    private static class PlayerUpdateEmotePatch
    {
        private static void Postfix(Player __instance)
        {
            bool inEmote = __instance.InEmote();

            if (!ShouldPreventEmoteCancel())
            {
                ComboState comboState = GetState(__instance);
                comboState.WasInEmote = inEmote;
                comboState.LastAttackPreventsEmoteCancel = false;
                comboState.EmoteInterruptedComboWindow = false;
                return;
            }

            ComboState state = GetState(__instance);
            if (!state.WasInEmote && inEmote && state.LastAttackPreventsEmoteCancel && __instance.GetTimeSinceLastAttack() <= ComboCarryResetWindow)
            {
                state.EmoteInterruptedComboWindow = true;
            }

            state.WasInEmote = inEmote;
        }
    }

    private static readonly HashSet<string> EmoteCommands = Enum.GetNames(typeof(Emotes))
        .Where(name => name != "Count")
        .Select(name => name.ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Reflection.FieldInfo? BindMapField = AccessTools.Field(typeof(Terminal), "m_binds");
    private static readonly System.Reflection.FieldInfo? BindListField = AccessTools.Field(typeof(Terminal), "m_bindList");

    private static bool ShouldBlockEmoteConsoleBinds()
    {
        return ServerSyncModTemplatePlugin.BlockEmoteConsoleBinds.Value.IsOn();
    }

    private static bool IsEmoteCommand(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return false;
        }

        string[] tokens = (commandText ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string? firstToken = tokens.Length > 0 ? tokens[0] : null;

        string commandName = firstToken == null ? "" : firstToken.TrimStart('/').ToLowerInvariant();

        return EmoteCommands.Contains(commandName);
    }

    private static bool TryGetBoundCommandFromBindInvocation(string text, out string boundCommand)
    {
        string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].TrimStart('/').Equals("bind", StringComparison.OrdinalIgnoreCase))
        {
            boundCommand = "";
            return false;
        }

        boundCommand = string.Join(" ", parts.Skip(2));
        return true;
    }

    private static Dictionary<KeyCode, List<string>>? GetConsoleBindMap()
    {
        return BindMapField?.GetValue(null) as Dictionary<KeyCode, List<string>>;
    }

    internal static void RefreshConsoleBinds()
    {
        if (ThreadingHelper.SynchronizingObject.InvokeRequired)
        {
            ThreadingHelper.Instance.StartSyncInvoke(RefreshConsoleBinds);
            return;
        }

        // Chat.Awake initializes the stored binds and calls updateBinds itself.
        if (BindListField?.GetValue(null) == null)
        {
            return;
        }

        AccessTools.DeclaredMethod(typeof(Terminal), "updateBinds")?.Invoke(null, null);
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.TryRunCommand))]
    private static class TerminalTryRunCommandPatch
    {
        private static bool Prefix(Terminal __instance, string text)
        {
            if (!ShouldBlockEmoteConsoleBinds() || !TryGetBoundCommandFromBindInvocation(text, out string boundCommand) || !IsEmoteCommand(boundCommand))
            {
                return true;
            }

            __instance.AddString("CancelAnimationCancels: binding emote commands is disabled.");
            return false;
        }
    }

    [HarmonyPatch(typeof(Terminal), "updateBinds")]
    private static class TerminalUpdateBindsPatch
    {
        private static void Postfix()
        {
            if (!ShouldBlockEmoteConsoleBinds())
            {
                return;
            }

            Dictionary<KeyCode, List<string>>? binds = GetConsoleBindMap();
            if (binds == null)
            {
                return;
            }

            foreach (KeyCode key in binds.Keys.ToList())
            {
                binds[key].RemoveAll(IsEmoteCommand);
                if (binds[key].Count == 0)
                {
                    binds.Remove(key);
                }
            }
        }
    }
}
