using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly Func<HextechEnemyHexContext, Player, Task> OriginalNativeVakuuControl =
        AccessTools.DeclaredMethod(typeof(ShoulderVakuEnemyHex), "TryControlSecondTurn")
            .CreateDelegate<Func<HextechEnemyHexContext, Player, Task>>();
    private sealed class NativeVakuuSelectorScope : IDisposable { public void Dispose() { } }
    private static void RegisterNativePlayerVakuu(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ShoulderVakuRune), "IsOddOwnerTurn"),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "IsDead"), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "CombatState"), nameof(NativeBranchCombat), 2));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ShoulderVakuRune), "OwnerHasWhisperingEarring"),
            Site(AccessTools.PropertyGetter(typeof(Player), "Relics"), nameof(NativeCapturedPlayerRelics)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ShoulderVakuRune), "AfterPlayerTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "MaxHp"), nameof(NativeBranchMaxHp)),
            Site(NativeHeal, nameof(HealNative)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ShoulderVakuRune), "ControlOddTurnWithVakuu"),
            Site(AccessTools.DeclaredMethod(typeof(VakuuTurnController), "PlayLineIfCardsPlayed"), nameof(NativeVakuuPresentation)));
        RegisterNativeTurnFlagRune<ShoulderVakuRune>(false);
        foreach (string name in new[] { "ModifyHandDraw", "ModifyMaxEnergy", "AfterAutoPrePlayPhaseEnteredLate" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ShoulderVakuRune), name));
    }

    private static IReadOnlyList<RelicModel> NativeCapturedPlayerRelics(Player player)
        => _simulator is null ? player.Relics : ((SimulatedCombatState)_simulator.State.CombatState).RelicsOf(player);
    private static void RegisterNativeVakuuControl(Harmony harmony)
    {
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(ShoulderVakuEnemyHex), "TryControlSecondTurn"),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "IsDead"), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "CombatState"), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(HextechEnemyHexContext), "Tracking"), nameof(NativeCapturedTracking)),
            Site(AccessTools.DeclaredMethod(typeof(VakuuTurnController), "PlayLineIfCardsPlayed"), nameof(NativeVakuuPresentation)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(VakuuTurnController), "AutoPlayPlayableHand"),
            Site(AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "CombatState"), nameof(NativeBranchCombat)),
            Site(AccessTools.PropertyGetter(typeof(CombatManager), "IsOverOrEnding"), nameof(NativeBranchEnding)),
            Site(AccessTools.Method(typeof(CombatManager), "IsPlayerReadyToEndTurn", [typeof(Player)]), nameof(NativeVakuuReady)),
            Site(AccessTools.PropertyGetter(typeof(CardPile), "Cards"), nameof(NativeBranchPileCards)),
            Site(AccessTools.Method(typeof(CardModel), "SpendResources", []), nameof(SpendNativeVakuuResources)),
            Site(AccessTools.DeclaredMethod(typeof(HextechAutoPlayHelper), "AutoPlayOrMoveToResultPile"), nameof(AutoPlayNativeCard)),
            Site(AccessTools.Method(typeof(CardSelectCmd), "PushSelector", [typeof(ICardSelector), typeof(bool)]), nameof(PushNativeVakuuSelector)));
        var canPlay = typeof(VakuuTurnController).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(AccessTools.GetDeclaredMethods).Single(method => method.Name.StartsWith("<AutoPlayPlayableHand>")
                && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(CardModel));
        PatchEventCallback(harmony, canPlay,
            Site(AccessTools.Method(typeof(CardModel), "CanPlay", []), nameof(NativeVakuuCanPlay)));
        foreach (string method in new[] { "GetTarget", "PickStableAllyTarget", "PlayLineIfCardsPlayed" })
            NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(VakuuTurnController), method));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(ShoulderVakuEnemyHex), "AfterAutoPrePlayPhaseEnteredLate"));
        var tail = AccessTools.DeclaredMethod(typeof(SimulatedCombatState), "ContinueAutoPrePlay");
        harmony.Patch(tail, transpiler: new HarmonyMethod(typeof(NativeRuneBridge), nameof(InsertNativeAutoPrePlayLate)));
        NativeCallbackContracts.Add(tail, AccessTools.Method(typeof(NativeRuneBridge), nameof(InsertNativeAutoPrePlayLate)));
        CompatibilityGuard.EnemyHexes.Add(MonsterHexKind.ShoulderVaku);
    }

    private static bool NativeVakuuCanPlay(CardModel card)
        => _simulator is null ? card.CanPlay()
            : ((SimulatedCombatState)_simulator.State.CombatState).CanPlayCard(_simulator,
                _simulator.State.FindCard(card) ?? throw new PredictionUnsupportedException("Vakuu candidate is absent from its branch."));
    private static bool NativeVakuuReady(CombatManager manager, Player player)
        => _simulator is null ? manager.IsPlayerReadyToEndTurn(player)
            : _simulator.State.GetPlayerCombatState(player).Phase is not (PlayerTurnPhase.AutoPrePlay or PlayerTurnPhase.Play);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<(int, int)> SpendNativeVakuuResources(CardModel card)
    {
        if (_simulator is null) return card.SpendResources();
        var owned = _simulator.State.FindCard(card)
            ?? throw new PredictionUnsupportedException("Vakuu payment card is absent from its branch.");
        var payment = _simulator.SpendResources(owned, isAutoPlay: false);
        PauseNativeChoice(_simulator);
        return Task.FromResult((payment.EnergySpent, payment.StarsSpent));
    }
    private static IDisposable PushNativeVakuuSelector(ICardSelector selector, bool flag)
        => _simulator is null ? CardSelectCmd.PushSelector(selector, flag) : new NativeVakuuSelectorScope();
    private static void NativeVakuuPresentation(Player player, int count)
    { if (_simulator is null) VakuuTurnController.PlayLineIfCardsPlayed(player, count); }

    private static bool AfterNativeAutoPrePlayLate(SimulatedCombatState combat,
        CombatPredictionSimulator simulator, Player player)
    {
        // Native Mayhem enters the player-play tracking phase before running
        // its late effects, after all earlier automatic play families finish.
        EnterNativeEnemyPlayPhase(simulator, player);
        foreach (var listener in combat.IterateHookListeners().ToArray())
        {
            if (listener is RelicModel { IsMelted: true }) continue;
            var modifier = listener as HextechMayhemModifier;
            var state = modifier is null ? null : ModelPredictionStateMirrors.Get<EnemyState>(simulator, modifier);
            if (listener is not ShoulderVakuRune && state?.Has(MonsterHexKind.ShoulderVaku) != true) continue;
            var cursor = TurnStartChoiceCursor.ForAutomaticPolicy(request => request.Spec is null
                ? null : CardChoiceSupport.BuildVakuuChoice(request.Spec));
            var previous = combat.OverrideActionChoices(cursor);
            try
            {
                if (listener is ShoulderVakuRune rune)
                    RequireCompleted(Invoke(rune, simulator, model => model.AfterAutoPrePlayPhaseEnteredLate(
                        new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), player)), typeof(ShoulderVakuRune));
                else InvokeNativeEnemyReaction(modifier!, state!, simulator, context => OriginalNativeVakuuControl(context, player));
            }
            finally { combat.RestoreActionChoices(cursor, previous); }
            if (simulator.HasPendingChoice) { simulator.RejectExecutionContinuation(); return false; }
        }
        return true;
    }
    private static IEnumerable<CodeInstruction> InsertNativeAutoPrePlayLate(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var setter = AccessTools.PropertySetter(typeof(SimPlayerCombatState), "Phase");
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(setter))
            {
                count++;
                var resume = generator.DefineLabel();
                var argument = new CodeInstruction(OpCodes.Ldarg_0).WithLabels(instruction.labels.ToArray());
                argument.blocks.AddRange(instruction.blocks); instruction.blocks.Clear(); instruction.labels.Clear();
                yield return argument;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(AfterNativeAutoPrePlayLate));
                yield return new CodeInstruction(OpCodes.Brtrue, resume);
                yield return new CodeInstruction(OpCodes.Pop);
                yield return new CodeInstruction(OpCodes.Pop);
                yield return new CodeInstruction(OpCodes.Ldc_I4_1);
                yield return new CodeInstruction(OpCodes.Ret);
                instruction.labels.Add(resume);
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Pinned automatic-play late phase boundary changed.");
    }
}
