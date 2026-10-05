using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static NativeCallSite SingleNativePowerSite<T>() where T : PowerModel
    {
        var method = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(m =>
            m.Name == "Apply" && m.IsGenericMethodDefinition && m.GetParameters().Length == 5
            && m.GetParameters()[0].ParameterType == typeof(Creature));
        return new(method.MakeGenericMethod(typeof(T)),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyPowerOne)).MakeGenericMethod(typeof(T)), 1);
    }

    private static void RegisterNativeDragonSouls(Harmony harmony)
    {
        var alive = AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive));
        RegisterNativePowerCard<CatalystCard>(harmony,
            new NativeCallSite(NativePowerAmount.MakeGenericMethod(typeof(PoisonPower)),
                BranchPowerAmount.MakeGenericMethod(typeof(PoisonPower)), 1), SingleNativePowerSite<PoisonPower>());
        RuneMirrors.RegisterNativeBase<CatalystRune>();

        RegisterNativePowerCard<OceanDragonSoulCard>(harmony, SingleNativePowerSite<HextechOceanDragonSoulPower>());
        RegisterNativePowerCard<MountainDragonSoulCard>(harmony, SingleNativePowerSite<HextechMountainDragonSoulPower>());
        RegisterNativePowerCard<HextechDragonSoulCard>(harmony, SingleNativePowerSite<HextechDragonSoulPower>());
        RegisterNativePowerCard<CloudDragonSoulCard>(harmony, SingleNativePowerSite<HextechCloudDragonSoulPower>());
        RuneMirrors.RegisterNativeBase<OceanDragonSoulRune>();
        RuneMirrors.RegisterNativeBase<HextechDragonSoulRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechOceanDragonSoulPower), "AfterTurnEnd"),
            Site(alive, nameof(NativeBranchIsAlive)), Site(NativeHeal, nameof(HealNative)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechMountainDragonSoulPower), "AfterSideTurnStart"),
            Site(alive, nameof(NativeBranchIsAlive)), SingleNativePowerSite<PlatingPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechDragonSoulPower), "AfterEnergyResetLate"),
            Site(alive, nameof(NativeBranchIsAlive)), Site(NativeGainEnergy, nameof(GainNativeEnergy)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechCloudDragonSoulPower), "ModifyHandDraw"),
            Site(alive, nameof(NativePowerProjectionIsAlive)));
        RegisterNativeSoulPower<HextechOceanDragonSoulPower>();
        RegisterNativeSoulPower<HextechMountainDragonSoulPower>();
        RegisterNativeSoulPower<HextechDragonSoulPower>();
        RegisterNativeSoulPower<HextechCloudDragonSoulPower>();
        NativeAfterSidePowers.Add(typeof(HextechMountainDragonSoulPower), (power, simulator, side, participants) =>
            InvokeNativePower((HextechMountainDragonSoulPower)power, simulator, model =>
                model.AfterSideTurnStartForParticipants(side, participants, simulator.State.CombatState)));
        // The existing single CorrosiveWave transpiler composes this insertion;
        // preserve one precisely audited transformation of the stock loop.
        NativeCallbackContracts.Add(AccessTools.Method(typeof(EndTurnPowerSupport), nameof(EndTurnPowerSupport.TriggerRegular)),
            AccessTools.Method(typeof(PowerExpiryBridge), "RewriteWaveRemoval"));
    }

    private static void RegisterNativePowerCard<T>(Harmony harmony, params NativeCallSite[] sites) where T : CardModel
    {
        var callback = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} native OnPlay missing.");
        PatchEventCallback(harmony, callback, sites);
        RegisterNativeTokenCard<T>(callback);
    }

    private static void RegisterNativeSoulPower<T>(bool beforeEndIgnored = true) where T : HextechPowerBase
    {
        BeforeSideTurnStartMirrors.Register<T>((_, _) => { });
        if (beforeEndIgnored) BeforeSideTurnEndMirrors.Registry.RegisterIgnored<T>();
        ModifyDamageMirrors.MultiplicativeRegistry.Register<T>((power, context) =>
            power.ModifyDamageMultiplicativeCompat(context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.Preview));
        CompatibilityGuard.Powers.Add(typeof(T));
    }

    private static bool NativePowerProjectionIsAlive(Creature creature)
    {
        if (_simulator is not null) return _simulator.State.GetCreature(creature).IsAlive;
        var combat = RuneProjectionMirrors.CurrentBranch;
        return combat is null ? creature.IsAlive : combat._predictionState!.GetCreature(creature).IsAlive;
    }

    private static void DispatchNativeEndPower(PowerModel power, CombatPredictionSimulator simulator,
        CombatSide side, IEnumerable<Creature> participants)
    {
        if (power is HextechOceanDragonSoulPower ocean)
            InvokeNativePower(ocean, simulator, model => model.AfterTurnEndForParticipants(
                new ThrowingPlayerChoiceContext(), side, participants));
    }

    internal static IEnumerable<CodeInstruction> RewriteNativeEndTurnPowers(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        var pending = AccessTools.PropertyGetter(typeof(CombatPredictionSimulator), nameof(CombatPredictionSimulator.HasPendingChoice));
        if (body.Count(i => i.Calls(pending)) != 2 || !body.Any(i => i.opcode == OpCodes.Stloc_3)
            || body.Count(i => i.Calls(AccessTools.Method(typeof(SimulatedCombatState), nameof(SimulatedCombatState.EffectivePowers)))) != 1)
            throw new InvalidOperationException("Pinned ordered regular end-power loop changed.");
        int inserted = 0;
        int join = body.FindLastIndex(i => i.Calls(pending)) - 1;
        if (join < 0 || body[join].opcode != OpCodes.Ldarg_0)
            throw new InvalidOperationException("Pinned end-power final pending-choice load changed.");
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (index == join)
            {
                var first = new CodeInstruction(OpCodes.Ldloc_3);
                first.labels.AddRange(instruction.labels); instruction.labels.Clear();
                yield return first;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Ldarg_3);
                yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(DispatchNativeEndPower));
                inserted++;
            }
            yield return instruction;
        }
        if (inserted != 1) throw new InvalidOperationException("Pinned end-power switch join changed.");
    }
}
