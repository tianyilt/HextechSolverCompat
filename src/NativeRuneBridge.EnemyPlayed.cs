using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Commands;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly MonsterHexKind[] NativeEnemyPlayedKinds =
        [MonsterHexKind.TanksShield, MonsterHexKind.AncientWine, MonsterHexKind.MonarchsGaze, MonsterHexKind.MirrorReflection, MonsterHexKind.Archmage,
         MonsterHexKind.Upgrade, MonsterHexKind.IGrip, MonsterHexKind.CorruptHeart];
    private sealed record NativeEnemyPlayedHandler(MonsterHexKind Kind, Type Type,
        Func<HextechEnemyHexContext, PlayerChoiceContext, CardPlay, Task> Callback);
    private static NativeEnemyPlayedHandler[] NativeEnemyPlayedEffects = [];

    private static void RegisterNativeEnemyPlayed(Harmony harmony)
    {
        var effects = (IReadOnlyList<HextechEnemyHexEffect>)AccessTools.Field(typeof(HextechEnemyHexEffects), "OrderedEffects").GetValue(null)!;
        var reviewed = effects.Where(effect => effect is TanksShieldEnemyHex or AncientWineEnemyHex
            or MonarchsGazeEnemyHex or MirrorReflectionEnemyHex or ArchmageEnemyHex or UpgradeEnemyHex
            or IGripEnemyHex or CorruptHeartEnemyHex or BackToBasicsEnemyHex or MasterOfDualityEnemyHex).ToArray();
        if (reviewed.Length != 10 || reviewed.Any(effect => AccessTools.GetDeclaredFields(effect.GetType()).Any(field => !field.IsStatic)))
            throw new InvalidOperationException("Reviewed enemy played catalogue changed.");
        NativeEnemyPlayedEffects = reviewed.Select(effect => new NativeEnemyPlayedHandler(
            AccessTools.PropertyGetter(effect.GetType(), "Kind").CreateDelegate<Func<MonsterHexKind>>(effect)(), effect.GetType(),
            AccessTools.DeclaredMethod(effect.GetType(), "AfterCardPlayed")
                .CreateDelegate<Func<HextechEnemyHexContext, PlayerChoiceContext, CardPlay, Task>>(effect))).ToArray();
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "IsManualPlayerCardPlay"),
            new NativeCallSite(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeBranchCombat)), 1));
        var alive = AccessTools.DeclaredMethod(typeof(HextechEnemyHexContext), "GetAliveEnemies");
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(TanksShieldEnemyHex), "AfterCardPlayed"),
            Site(alive, nameof(NativeContextAliveEnemies)), Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(AncientWineEnemyHex), "AfterCardPlayed"),
            Site(alive, nameof(NativeContextAliveEnemies)), Site(NativeHeal, nameof(HealNative)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MonarchsGazeEnemyHex), "AfterCardPlayed"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            SingleNativePowerSite<HextechTemporaryStrengthLossPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(MirrorReflectionEnemyHex), "AfterCardPlayed"),
            Site(AccessTools.Method(typeof(ICombatState), nameof(ICombatState.CloneCard)), nameof(NativeEnemyCloneCard)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        RegisterNativeEnemyCardCosts(harmony);
        CompatibilityGuard.EnemyHexes.UnionWith(NativeEnemyPlayedKinds);
    }

    private static CardModel NativeEnemyCloneCard(ICombatState combat, CardModel card)
        => _simulator is null ? combat.CloneCard(card) : CloneNativeGeneratedCard(card);

    internal static void DispatchNativeEnemyPlayed(HextechMayhemModifier modifier, EnemyState state,
        CombatPredictionSimulator simulator, CardPlay play)
        => InvokeNativeEnemyReaction(modifier, state, simulator, async context =>
        {
            foreach (var effect in NativeEnemyPlayedEffects)
                if (state.Has(effect.Kind))
                    await effect.Callback(context, new ThrowingPlayerChoiceContext(), play);
        });
}
