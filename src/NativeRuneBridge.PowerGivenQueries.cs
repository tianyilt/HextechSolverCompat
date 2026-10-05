using CombatSolver;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativePowerGivenQueries(Harmony harmony)
    {
        RegisterState<BattleTranceUpgradeRune>();
        RegisterState<BulletTimeUpgradeRune>();
        RuneMirrors.RegisterNativeBase<BattleTranceUpgradeRune>();
        RuneMirrors.RegisterNativeBase<BulletTimeUpgradeRune>();
        harmony.Patch(AccessTools.Method(typeof(PersistentRelicSupport), nameof(PersistentRelicSupport.ModifyPowerAmountGiven)),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(ModifyNativeNoDrawGiven)));
    }

    private static void ModifyNativeNoDrawGiven(SimulatedCombatState combat, PowerModel power, Creature giver,
        Creature target, CardModel? cardSource, ref int __result)
    {
        // These two reviewed pure callbacks only inspect NoDraw, giver/target
        // identities and the actual source card's type/owner. Their exact native
        // predicates run on captured relics; no current live inventory is queried.
        if (power is not NoDrawPower || giver.Player is not { } player) return;
        decimal multiplier = 1m;
        foreach (var relic in combat.RelicsOf(player))
        {
            if (relic is not BattleTranceUpgradeRune && relic is not BulletTimeUpgradeRune) continue;
            decimal value = relic.ModifyPowerAmountGivenMultiplicative(power, giver, __result, target, cardSource);
            if (value is not (0m or 1m))
                throw new InvalidOperationException("Reviewed native NoDraw query ceased to be a binary multiplier.");
            multiplier *= value;
        }
        if (multiplier == 0m) __result = 0;
    }
}
