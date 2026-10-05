using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Cards;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeGeneratedTokens(Harmony harmony)
    {
        var combat = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState));
        RegisterNativePowerCard<OkBoomerangCard>(harmony,
            Site(combat, nameof(NativeBranchCombat), 2),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.TriggerAnim),
                [typeof(Creature), typeof(string), typeof(float)]), nameof(QuantumAnim)),
            Site(AccessTools.Method(typeof(Cmd), nameof(Cmd.CustomScaledWait),
                [typeof(float), typeof(float), typeof(bool), typeof(CancellationToken)]), nameof(QuantumWait)),
            Site(AccessTools.Method(typeof(HextechCombatVfx), "BoomerangSweep"), nameof(NativeBoomerangSweep)),
            Site(AccessTools.PropertyGetter(typeof(RelicModel), nameof(RelicModel.Icon)), nameof(NativeBoomerangIcon)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(OkBoomerangCard), "StrikeIfHittable"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(combat, nameof(NativeBranchCombat)), Site(NativeAttackExecute, nameof(ExecuteNativeAttack)));
        var locations = (MethodMirrorRegistry<CardModel, CardResultLocationMirrorContext, CardLocation>)
            AccessTools.DeclaredField(typeof(CardResultLocationMirrors), "Registry").GetValue(null)!;
        locations.Register<OkBoomerangCard>((_, context) =>
        {
            // The audited native override unconditionally sets exactly these
            // two fields after the base exhaust/duplicate handling.
            var result = context.BaseResult;
            result.pileType = PileType.Hand;
            result.position = CardPilePosition.Bottom;
            return result;
        });
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(OkBoomerangCard), "GetResultLocationForCardPlay"));
        RegisterNativePowerCard<OstyWishCard>(harmony,
            Site(combat, nameof(NativeBranchCombat), 2), Site(NativeDamageVar, nameof(DamageNativeVar)),
            Site(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.GainBlock),
                [typeof(Creature), typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar), typeof(CardPlay), typeof(bool)]),
                nameof(GainNativeVarBlock)));
    }

    private static Godot.Texture2D NativeBoomerangIcon(RelicModel relic)
        => _simulator is null ? relic.Icon : null!;

    private static void NativeBoomerangSweep(Creature owner, IReadOnlyList<Creature> enemies, Godot.Texture2D icon, bool roundTrip)
    {
        if (_simulator is null) HextechCombatVfx.BoomerangSweep(owner, enemies, icon, roundTrip);
    }
}
