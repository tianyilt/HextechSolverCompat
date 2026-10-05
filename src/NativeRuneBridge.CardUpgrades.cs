using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterSimpleCardUpgrades(Harmony harmony)
    {
        PatchPowerCallback<KnowThyPlaceUpgradeRune>(harmony, nameof(HextechRelicBase.AfterCardPlayed), 2, 2);
        RegisterAfterCardPlayed<KnowThyPlaceUpgradeRune>();
        PatchPowerCallback<ShriekUpgradeRune>(harmony, nameof(HextechRelicBase.AfterCardPlayed), 2, 1);
        RegisterAfterCardPlayed<ShriekUpgradeRune>();
        PatchPowerCallback<HotfixUpgradeRune>(harmony, nameof(HextechRelicBase.AfterCardPlayed), 2, 1);
        RegisterAfterCardPlayed<HotfixUpgradeRune>();
        var soul = AccessTools.DeclaredMethod(typeof(SoulUpgradeRune), nameof(HextechRelicBase.AfterCardPlayed));
        var soulMachine = soul.GetCustomAttributes(typeof(AsyncStateMachineAttribute), false)
            .Cast<AsyncStateMachineAttribute>().Single().StateMachineType;
        harmony.Patch(AccessTools.Method(soulMachine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteEnergyGain)));
        RegisterAfterCardPlayed<SoulUpgradeRune>();
        var bodyguard = AccessTools.DeclaredMethod(typeof(BodyguardUpgradeRune), nameof(HextechRelicBase.AfterCardPlayed));
        var bodyguardMachine = bodyguard.GetCustomAttributes(typeof(AsyncStateMachineAttribute), false)
            .Cast<AsyncStateMachineAttribute>().Single().StateMachineType;
        harmony.Patch(AccessTools.Method(bodyguardMachine, "MoveNext"),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(RewriteHeal)));
        RegisterAfterCardPlayed<BodyguardUpgradeRune>();
        RegisterAfterCardPlayed<ParticleWallUpgradeRune>();
        RegisterAfterCardCommandFamily(harmony);
        RegisterPersistentKeywordCards(harmony);
        RegisterOpeningFormCards();
        // Native Bodyguard's target may be an Osty created/revived in this branch.
        PatchGetter(harmony, typeof(Player), nameof(Player.Osty), nameof(OstyGetter));
    }

    private static bool OstyGetter(Player __instance, ref Creature? __result)
    {
        if (_simulator is null) return true;
        // GetOsty falls back to the live property for root identities. Temporarily
        // leave native callback scope while resolving that read-only identity.
        var simulator = _simulator;
        _simulator = null;
        try { __result = ((SimulatedCombatState)simulator.State.CombatState).GetOsty(__instance); }
        finally { _simulator = simulator; }
        return false;
    }
}
