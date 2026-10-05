using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private delegate void NativeForgeBonus(ref decimal amount, Player player, AbstractModel? source);
    private static NativeForgeBonus? _nativeForgeBonus;

    private static void RegisterNativeForge(Harmony harmony)
    {
        var patch = typeof(BigHammerRune).GetNestedType("BigHammerPatch", System.Reflection.BindingFlags.NonPublic)!;
        var prefix = AccessTools.DeclaredMethod(patch, "Prefix");
        var getRelic = AccessTools.GetDeclaredMethods(typeof(Player)).Single(method => method.Name == "GetRelic" && method.IsGenericMethodDefinition);
        PatchEventCallback(harmony, prefix, new NativeCallSite(getRelic.MakeGenericMethod(typeof(BigHammerRune)),
            AccessTools.Method(typeof(NativeRuneBridge), nameof(NativeBigHammer)), 2));
        _nativeForgeBonus = prefix.CreateDelegate<NativeForgeBonus>();
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(BigHammerRune), "ApplyForgeBonus"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(BigHammerRune), "CalculateForgeAmount"));
        RegisterState<BigHammerRune>(); RuneMirrors.RegisterNativeBase<BigHammerRune>();

        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(KingdomArmyRune), "AfterForge"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsDead)), nameof(NativeBranchIsDead)),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.CombatState)), nameof(NativeBranchCombat)),
            Site(AccessTools.DeclaredMethod(typeof(HextechCardGeneration), "AddGeneratedCardToCombat"), nameof(AddNativeGeneratedCard)));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(KingdomArmyRune), "BeforeCombatStart"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(KingdomArmyRune), "AfterCombatEnd"));
        RegisterStableState<KingdomArmyRune>(); RuneMirrors.RegisterNativeBase<KingdomArmyRune>();
        RegisterNativeSdkPrefix(harmony, AccessTools.DeclaredMethod(typeof(PersistentPowerSupport), "Forge"), nameof(NativeSdkForge));
    }

    private static BigHammerRune? NativeBigHammer(Player player)
    {
        if (_simulator is null) return player.GetRelic<BigHammerRune>();
        var rune = ((SimulatedCombatState)_simulator.State.CombatState).RelicsOf(player).OfType<BigHammerRune>().FirstOrDefault();
        return rune is null ? null : (BigHammerRune)ModelPredictionStateMirrors.Get<NativeRuneState>(_simulator, rune).Model;
    }

    private static bool NativeSdkForge(CombatPredictionSimulator simulator, Player player, int amount)
    {
        if (!((SimulatedCombatState)simulator.State.CombatState).RelicsOf(player).Any(rune => rune is BigHammerRune or KingdomArmyRune)) return true;
        ForgeOwnedNativeBlade(simulator, amount, player, null);
        return false;
    }

    private static IEnumerable<SovereignBlade> ForgeOwnedNativeBlade(CombatPredictionSimulator simulator, decimal amount, Player player, AbstractModel? source)
    {
        if (simulator.HasPendingChoice || simulator.IsOverOrEnding) return [];
        var previous = _simulator;
        _simulator = simulator;
        try { _nativeForgeBonus!(ref amount, player, source); }
        finally { _simulator = previous; }
        var state = simulator.State.GetPlayerCombatState(player);
        if (!state.AllCards.Any(card => card.Preview is SovereignBlade && !card.Preview.IsDupe && !state.ExhaustPile.Cards.Contains(card)))
        {
            var created = PredictedCard.Create(CanonicalModels.Card<SovereignBlade>(), player);
            ((SovereignBlade)created.MutablePreview).CreatedThroughForge = true;
            simulator.AddGeneratedCardToCombat(created, PileType.Hand, player, CardPilePosition.Bottom, CardGenerationResultKind.Fixed);
            if (simulator.HasPendingChoice) return [];
        }
        // Capture the same blade membership as the native command before its
        // AfterForge listeners can generate additional cards or forge again.
        var blades = state.AllCards.Where(card => card.Preview is SovereignBlade && !card.Preview.IsDupe)
            .Select(card => (SovereignBlade)card.MutablePreview).ToArray();
        foreach (var blade in blades) blade.AddDamage(amount);
        foreach (var listener in ((SimulatedCombatState)simulator.State.CombatState).IterateHookListeners().ToArray())
        {
            if (listener is KingdomArmyRune { IsMelted: false } rune)
                RequireCompleted(Invoke(rune, simulator, owned => owned.AfterForge(amount, player, source)), typeof(KingdomArmyRune));
        }
        PauseNativeChoice(simulator);
        return blades;
    }
}
