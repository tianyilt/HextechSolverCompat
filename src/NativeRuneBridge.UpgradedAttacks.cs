using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Cards;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly Dictionary<Type, Action<HextechRelicBase, CardOnPlayMirrorContext>> ConditionalPlays = [];
    private static readonly Dictionary<Type, Type> CompleteNativeCards = [];
    private static readonly MethodInfo NativeContextPower = AccessTools.GetDeclaredMethods(typeof(HextechPowerCmdCompat)).Single(method =>
        method.Name == "Apply" && method.IsGenericMethodDefinition
        && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
            new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool) }));
    private static void RegisterNativeUpgradedAttacks(Harmony harmony)
    {
        var combat = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.CombatState));
        PatchEventCallback(harmony, AccessTools.Method(typeof(GrandFinaleUpgradeRune), "PlayUpgradedSafely"),
            Site(combat, nameof(NativeUpgradedCardCombat)), Site(NativeAttackExecute, nameof(ExecuteNativeAttack)));
        RegisterConditionalPlay<GrandFinale, GrandFinaleUpgradeRune>((_, context) => GrandFinaleUpgradeRune.PlayUpgradedSafely(
            new ThrowingPlayerChoiceContext(), (GrandFinale)context.Card.MutablePreview));
        PatchEventCallback(harmony, AccessTools.Method(typeof(CrashLandingUpgradeRune), "PlayUpgraded"),
            Site(combat, nameof(NativeUpgradedCardCombat)), Site(NativeAttackExecute, nameof(ExecuteNativeAttack)),
            Site(AccessTools.Method(typeof(CardPile), nameof(CardPile.GetCards), [typeof(Player), typeof(PileType[])]), nameof(NativeUpgradedPileCards), 2),
            Site(AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.AddGeneratedCardsToCombat),
                [typeof(IEnumerable<CardModel>), typeof(PileType), typeof(Player), typeof(CardPilePosition)]), nameof(AddNativeGeneratedBatch)));
        RegisterConditionalPlay<CrashLanding, CrashLandingUpgradeRune>((_, context) => CrashLandingUpgradeRune.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (CrashLanding)context.Card.MutablePreview, context.CardPlay));
        PatchEventCallback(harmony, AccessTools.Method(typeof(WroughtInWarUpgradeRune), "PlayUpgraded"),
            Site(NativeAttackExecute, nameof(ExecuteNativeAttack)), Site(NativeDecimalBlock, nameof(GainNativeBlock)),
            Site(AccessTools.Method(typeof(ForgeCmd), nameof(ForgeCmd.Forge),
                [typeof(decimal), typeof(Player), typeof(AbstractModel)]), nameof(ForgeNativeBlade)));
        RegisterConditionalPlay<WroughtInWar, WroughtInWarUpgradeRune>((model, context) => model.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (WroughtInWar)context.Card.MutablePreview, context.CardPlay));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(WroughtInWarUpgradeRune), "CalculateFisticuffsBlock"));
        PatchEventCallback(harmony, AccessTools.Method(typeof(HangUpgradeRune), "PlayUpgraded"),
            Site(NativeAttackExecute, nameof(ExecuteNativeAttack)),
            new(NativePowerAmount.MakeGenericMethod(typeof(HextechHangPower)), BranchPowerAmount.MakeGenericMethod(typeof(HextechHangPower)), 1),
            new(NativeContextPower.MakeGenericMethod(typeof(HextechHangPower)),
                AccessTools.Method(typeof(NativeRuneBridge), nameof(ApplyNativeContextPower)).MakeGenericMethod(typeof(HextechHangPower)), 1));
        RegisterConditionalPlay<Hang, HangUpgradeRune>((_, context) => HangUpgradeRune.PlayUpgraded(
            new ThrowingPlayerChoiceContext(), (Hang)context.Card.MutablePreview, context.CardPlay));
        RegisterPassiveNativePower<HextechHangPower>();
        NativeCallbackContracts.Add(AccessTools.Method(typeof(HangUpgradeRune), "NextIncrease"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HangUpgradeRune), "TryModifyKeywordsInCombat"));
        var gate = AccessTools.Method(typeof(CardIsPlayableMirrors), "HandleGrandFinale");
        var rewrite = AccessTools.Method(typeof(NativeRuneBridge), nameof(RewriteGrandFinaleGate));
        harmony.Patch(gate, transpiler: new HarmonyMethod(rewrite));
        NativeCallbackContracts.Add(gate, rewrite);
        var nativeGate = AccessTools.PropertyGetter(typeof(GrandFinale), "IsPlayable");
        var nativePatch = AccessTools.Method(AccessTools.Inner(typeof(GrandFinaleUpgradeRune), "GrandFinalePlayablePatch"), "Postfix");
        NativeCallbackContracts.AddNativePostfix(nativeGate, nativePatch, "Natsuki.HextechRunes", Priority.Normal);
        NativeCallbackContracts.Add(AccessTools.Method(typeof(GrandFinaleUpgradeRune), "AllowsPlaying"));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(CrashLandingUpgradeRune), "ShouldUseUpgradedPlay"));
    }
    private static Task<T?> ApplyNativeContextPower<T>(PlayerChoiceContext? context, Creature target, decimal amount,
        Creature? applier, CardModel? source, bool silent) where T : PowerModel
        => _simulator is null ? HextechPowerCmdCompat.Apply<T>(context, target, amount, applier, source, silent)
            : ApplyPowerOne<T>(target, amount, applier, source, silent);
    private static void RegisterPassiveNativePower<T>() where T : HextechPowerBase
    {
        foreach (var (name, parameters) in new[] { ("BeforeSideTurnStart", 3), ("AfterSideTurnStartForParticipants", 3), ("BeforeTurnEnd", 2), ("AfterTurnEnd", 2) })
        {
            var method = typeof(T).GetMethods().Single(method => method.Name == name && method.GetParameters().Length == parameters);
            if (method.DeclaringType != typeof(HextechPowerBase))
                throw new InvalidOperationException($"Passive native power acquired an active lifecycle: {typeof(T).Name}.{name}.");
        }
        BeforeSideTurnStartMirrors.Register<T>((_, _) => { });
        BeforeSideTurnEndMirrors.Registry.RegisterIgnored<T>();
        ModifyDamageMirrors.MultiplicativeRegistry.Register<T>((power, context) => power.ModifyDamageMultiplicativeCompat(
            context.Target, context.Amount, context.Props, context.Dealer, context.CardSource?.Preview));
        NativeCallbackContracts.Add(AccessTools.Method(typeof(T), "ModifyDamageMultiplicativeCompat"));
        CompatibilityGuard.Powers.Add(typeof(T));
    }
    private static void RegisterConditionalPlay<TCard, T>(Func<T, CardOnPlayMirrorContext, Task> callback, bool stableGeneration = false)
        where TCard : CardModel where T : HextechRelicBase
    {
        if (stableGeneration) RegisterStableState<T>(); else RegisterState<T>();
        RuneMirrors.RegisterNativeBase<T>();
        CompleteNativeCards.Add(typeof(TCard), typeof(T));
        ConditionalPlays.Add(typeof(T), (rune, context) => RequireCompleted(
            Invoke((T)rune, context.Simulator, model => callback(model, context)), typeof(T)));
    }
    private static bool HasCompleteNativeCardBody(SimulatedCombatState combat, PredictedCard card)
        => CompleteNativeCards.TryGetValue(card.Preview.GetType(), out var runeType)
            && combat.RelicsOf(card.Preview.Owner).Any(rune => rune.GetType() == runeType);
    internal static bool TryPlayConditionalRune(HextechRelicBase rune, CardOnPlayMirrorContext context)
    {
        if (!ConditionalPlays.TryGetValue(rune.GetType(), out var callback)) return false;
        callback(rune, context); return true;
    }
    private static ICombatState? NativeUpgradedCardCombat(CardModel card)
        => _simulator?.State.CombatState ?? card.CombatState;
    private static IEnumerable<CardModel> NativeUpgradedPileCards(Player player, PileType[] piles)
        => _simulator is null ? CardPile.GetCards(player, piles)
            : piles.SelectMany(pile => NativeBranchPileCards(pile.GetPile(player))).ToArray();
    private static Task<IReadOnlyList<CardPileAddResult>> AddNativeGeneratedBatch(IEnumerable<CardModel> cards,
        PileType pile, Player? creator, CardPilePosition position)
    {
        if (_simulator is null) return CardPileCmd.AddGeneratedCardsToCombat(cards, pile, creator, position);
        var generated = cards.ToArray();
        if (generated.Any(card => !card.IsMutable || _simulator.State.FindCard(card) is not null))
            throw new PredictionUnsupportedException("Native batch generator supplied live or inserted cards.");
        var results = _simulator.AddGeneratedCardsToCombat(generated.Select(PredictedCard.FromGenerated).ToArray(),
            pile, creator, position, CardGenerationResultKind.Fixed);
        PauseNativeChoice(_simulator);
        return Task.FromResult<IReadOnlyList<CardPileAddResult>>(results.Select((result, index) => new CardPileAddResult
            { success = result.Success, cardAdded = generated[index], oldPile = null, targetPile = pile, modifyingModels = [] }).ToArray());
    }
    private static bool NativeGrandFinaleGate(GrandFinale card, CardIsPlayableMirrorContext context)
        => ((SimulatedCombatState)context.CombatState).RelicsOf(card.Owner).OfType<GrandFinaleUpgradeRune>().Any();
    private static IEnumerable<CodeInstruction> RewriteGrandFinaleGate(IEnumerable<CodeInstruction> instructions)
    {
        int returns = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ret)
            {
                var start = new CodeInstruction(OpCodes.Ldarg_0);
                start.labels.AddRange(instruction.labels); instruction.labels.Clear();
                start.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return start;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return CodeInstruction.Call(typeof(NativeRuneBridge), nameof(NativeGrandFinaleGate));
                yield return new CodeInstruction(OpCodes.Or);
                returns++;
            }
            yield return instruction;
        }
        if (returns != 1) throw new InvalidOperationException($"GrandFinale stock gate changed: returns={returns}.");
    }
}
