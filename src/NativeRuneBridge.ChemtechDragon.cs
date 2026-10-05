using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly ConditionalWeakTable<SimulatedCombatState, NativePotionGenerationState> PotionGenerationStates = new();
    [ThreadStatic] private static NativePotionGenerationState? _nativePotionGeneration;

    internal static void BindNativePotionGeneration(SimulatedCombatState combat, NativePotionGenerationState state)
        => PotionGenerationStates.Add(combat, state);

    private static void RegisterNativeChemtechDragon(Harmony harmony)
    {
        RegisterNativePowerCard<ChemtechDragonSoulCard>(harmony, SingleNativePowerSite<HextechChemtechDragonSoulPower>());
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechChemtechDragonSoulPower), "AfterSideTurnStart"),
            Site(AccessTools.PropertyGetter(typeof(Creature), nameof(Creature.IsAlive)), nameof(NativeBranchIsAlive)),
            Site(AccessTools.DeclaredMethod(typeof(HextechGameApiCompat), "GetPotionOptions"), nameof(NativeChemtechPotionOptions)),
            Site(AccessTools.Method(typeof(PotionCmd), nameof(PotionCmd.TryToProcure),
                [typeof(PotionModel), typeof(Player), typeof(int)]), nameof(NativeChemtechProcurePotion)));
        RegisterNativeSoulPower<HextechChemtechDragonSoulPower>();
        NativeAfterSidePowers.Add(typeof(HextechChemtechDragonSoulPower), (power, simulator, side, _) =>
        {
            var previous = _nativePotionGeneration;
            if (!PotionGenerationStates.TryGetValue((SimulatedCombatState)simulator.State.CombatState, out var frozen))
                throw new PredictionUnsupportedException("Chemtech potion generation has no captured root inputs.");
            _nativePotionGeneration = frozen;
            try
            {
                InvokeNativePower((HextechChemtechDragonSoulPower)power, simulator,
                    model => model.AfterSideTurnStart(side, simulator.State.CombatState));
            }
            finally { _nativePotionGeneration = previous; }
        });
    }

    private static IEnumerable<PotionModel> NativeChemtechPotionOptions(Player player)
        => _simulator is null ? HextechGameApiCompat.GetPotionOptions(player)
            : _nativePotionGeneration?.Candidates ?? throw new PredictionUnsupportedException("Missing Chemtech potion pool scope.");

    private static Task<PotionProcureResult> NativeChemtechProcurePotion(PotionModel potion, Player player, int slot)
    {
        if (_simulator is null) return PotionCmd.TryToProcure(potion, player, slot);
        if (slot != -1) throw new PredictionUnsupportedException("Chemtech requested an unaudited explicit potion slot.");
        bool success = ((SimulatedCombatState)_simulator.State.CombatState).TryProcurePotion(player, potion);
        PauseNativeChoice(_simulator);
        // This single audited caller discards the entire result immediately
        // after awaiting. Procurement hooks, full slots and slots themselves
        // are resolved by the solver's native potion command implementation.
        return Task.FromResult(new PotionProcureResult { success = success, potion = potion });
    }
}
