using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeSummonResources(Harmony harmony)
    {
        RegisterState<BigHandsRune>(); RuneMirrors.RegisterNativeBase<BigHandsRune>();
        RegisterState<DrainRune>(); RuneMirrors.RegisterNativeBase<DrainRune>();
        RegisterState<BoneGuardRune>(); RegisterState<PlasterRune>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(BigHandsRune), "ModifySummonAmount"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(BigHandsRune), "CalculateSummonAmount"));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(BoneGuardRune), "AfterSummon"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(NativeDecimalBlock, nameof(GainNativeBlock)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(PlasterRune), "AfterSummon"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Player), nameof(Player.IsOstyAlive)), nameof(NativeBranchOstyAlive)),
            Site(AccessTools.PropertyGetter(typeof(Player), nameof(Player.Osty)), nameof(NativeBranchOsty), 3),
            Site(NativeHeal, nameof(HealNative)));
        var apply = AccessTools.GetDeclaredMethods(typeof(PowerCmd)).Single(m => m.Name == "Apply" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 6 && m.GetParameters()[1].ParameterType == typeof(IEnumerable<Creature>));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(DrainRune), "AfterSummon"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat), 2),
            new NativeCallSite(apply.MakeGenericMethod(typeof(DoomPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeSummonDoom)), 1));
    }

    internal static decimal ModifyCapturedSummon(SimulatedCombatState combat, CombatPredictionSimulator simulator, Player player, int amount)
    {
        if (amount <= 0) return amount;
        decimal result = amount;
        foreach (var rune in combat.IterateHookListeners().OfType<BigHandsRune>())
            if (!rune.IsMelted) result = Invoke(rune, simulator, model => model.ModifySummonAmount(player, result, null));
        return result;
    }

    internal static void InvokeCapturedSummon(HextechRelicBase rune, CombatPredictionSimulator simulator, Player player, decimal amount)
    {
        if (rune.IsMelted) return;
        RequireCompleted(Invoke(rune, simulator, model => model.AfterSummon(new ThrowingPlayerChoiceContext(), player, amount)), rune.GetType());
    }

    private static async Task<IReadOnlyList<DoomPower>> ApplyNativeSummonDoom(PlayerChoiceContext context,
        IEnumerable<Creature> targets, decimal amount, Creature? applier, CardModel? source, bool silent)
    {
        if (_simulator is null) return await PowerCmd.Apply<DoomPower>(context, targets, amount, applier, source, silent);
        // Audited 0.9.7 modifiers only convert attributes, NoDraw, negative
        // Poison and Plating; none scales positive Doom. This original command
        // has no card source, so the stock Unsettling Lamp multiplier is inert.
        if (source is not null || amount < 0m || amount > int.MaxValue)
            throw new PredictionUnsupportedException("Native summon Doom command left its audited amount/source contract.");
        return await ApplyPowerMany<DoomPower>(targets, decimal.Truncate(amount), applier, null, silent);
    }
}
