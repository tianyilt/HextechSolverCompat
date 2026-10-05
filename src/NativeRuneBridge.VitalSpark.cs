using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static void RegisterNativeVitalSpark(Harmony harmony)
    {
        var type = typeof(HextechVitalSparkPower);
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "OwnerSkillCards"),
            Site(AccessTools.PropertyGetter(typeof(PlayerCombatState), "AllCards"), nameof(NativeRemovalAllCards)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "NativeAmount"),
            Site(AccessTools.PropertyGetter(typeof(Creature), "CombatState"), nameof(NativeBranchCombat)));
        var powersLambda = type.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(AccessTools.GetDeclaredMethods).Single(method => method.Name.StartsWith("<NativeAmount>")
                && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(Creature));
        PatchEventCallback(harmony, powersLambda,
            Site(AccessTools.PropertyGetter(typeof(Creature), "Powers"), nameof(NativeRemovalPowers)));
        var afflict = AccessTools.GetDeclaredMethods(typeof(CardCmd)).Single(method => method.Name == "Afflict"
            && method.IsGenericMethodDefinition && method.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Tainted));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "SyncCard"),
            Site(AccessTools.Method(typeof(CardCmd), "ClearAffliction"), nameof(ClearNativeAffliction)),
            new(afflict, AccessTools.Method(typeof(NativeRuneBridge), nameof(AfflictNativeTainted)), 1));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(type, "AfterCardPlayed"),
            new NativeCallSite(NativeContextPower.MakeGenericMethod(typeof(TaintedPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeContextPower)).MakeGenericMethod(typeof(TaintedPower)), 1));
        foreach (string method in new[] { "AfterApplied", "BeforeCombatStart", "AfterCardEnteredCombat",
            "AfterPowerAmountChanged", "AfterRemoved", "AfflictOwnerSkillCards", "SyncOwnerCards" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(type, method));
        RegisterNativeSoulPower<HextechVitalSparkPower>();
        NativeRemovalTypes.Add(type);
        AfterCardPlayedMirrors.Registry.Register<HextechVitalSparkPower>((power, context) =>
            InvokeNativePower(power, context.Simulator, model => model.AfterCardPlayed(
                new ThrowingPlayerChoiceContext(), context.CardPlay)));
        var normalize = AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "NormalizePowerAfflictions");
        harmony.Patch(normalize,
            prefix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(BeforeNativeVitalNormalization)),
            postfix: new HarmonyMethod(typeof(NativeRuneBridge), nameof(AfterNativeVitalNormalization)),
            transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(ProtectNativeVitalAfflictions)));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.ArcanePunch);
    }

    private static Task<Tainted?> AfflictNativeTainted(CardModel card, decimal amount)
    {
        if (_simulator is null) return CardCmd.Afflict<Tainted>(card, amount);
        var owned = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Vital Spark referenced an absent combat card.");
        return Task.FromResult(_simulator.Afflict<Tainted>(owned, amount));
    }

    private static void BeforeNativeVitalNormalization(SimulatedCombatState __instance, out int? __state)
        => __state = __instance._lastNormalizedVitalSparkAmount;

    private static void AfterNativeVitalNormalization(SimulatedCombatState __instance,
        CombatPredictionSimulator simulator, int? __state)
    {
        // This is the original compatibility postfix's refresh boundary. The
        // SDK already performed vanilla amount-change normalization above.
        if (__state is null || __state == __instance._lastNormalizedVitalSparkAmount) return;
        foreach (var power in __instance.EffectivePowers().OfType<HextechVitalSparkPower>())
            if (power.Amount > 0)
                InvokeNativePower(power, simulator, model => model.AfflictOwnerSkillCards());
    }

    private static IEnumerable<CodeInstruction> ProtectNativeVitalAfflictions(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        var clear = AccessTools.Method(typeof(CombatPredictedCardExtensions), "ClearAffliction");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(clear))
            {
                count++;
                var argument = new CodeInstruction(OpCodes.Ldarg_1).WithLabels(instruction.labels.ToArray());
                argument.blocks.AddRange(instruction.blocks);
                instruction.blocks.Clear();
                yield return argument;
                instruction.labels.Clear();
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NativeRuneBridge), nameof(ClearVanillaVitalAffliction));
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Pinned vanilla Tainted clearing boundary changed.");
    }

    private static void ClearVanillaVitalAffliction(PredictedCard card, CombatPredictionSimulator simulator)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        if (combat.GetAmount<HextechVitalSparkPower>(card.Preview.Owner.Creature) <= 0)
            card.ClearAffliction();
    }
}
