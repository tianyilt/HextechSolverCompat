using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace HextechSolverCompat;

internal static partial class NativeRuneBridge
{
    private static readonly Dictionary<Type, Action<RelicModel, CombatPredictionSimulator, CombatSide, IReadOnlyList<Creature>>> AuxiliarySideStart = [];

    private static void RegisterNativeOrobasRelics(Harmony harmony)
    {
        RegisterNativeOrobasState<HextechBlackBloodPlus>(); RegisterNativeOrobasState<HextechDivineDestinyPlus>();
        RegisterNativeOrobasState<HextechInfusedCorePlus>(); RegisterNativeOrobasState<HextechPhylacteryUnboundPlus>();
        RegisterNativeOrobasState<HextechRingOfTheDrakePlus>();
        var turn = AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.TurnNumber));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechDivineDestinyPlus), "AfterSideTurnStart"),
            Site(turn, nameof(NativeBranchTurnNumber)), Site(AccessTools.Method(typeof(PlayerCmd), "GainStars"), nameof(GainNativeStars)));
        var channel = AccessTools.GetDeclaredMethods(typeof(OrbCmd)).Single(method => method.Name == "Channel"
            && method.IsGenericMethodDefinition && method.GetParameters().Length == 2).MakeGenericMethod(typeof(LightningOrb));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechInfusedCorePlus), "AfterSideTurnStart"),
            Site(turn, nameof(NativeBranchTurnNumber)), Site(channel, nameof(ChannelNativeOrobasLightning)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechPhylacteryUnboundPlus), "AfterSideTurnStart"),
            Site(AccessTools.Method(typeof(OstyCmd), "Summon",
                [typeof(PlayerChoiceContext), typeof(Player), typeof(decimal), typeof(AbstractModel)]), nameof(SummonNativeForIgnoredResult)));
        PatchEventCallback(harmony, AccessTools.DeclaredMethod(typeof(HextechRingOfTheDrakePlus), "ModifyHandDraw"),
            Site(turn, nameof(NativeBranchTurnNumber)));
        RegisterNativeAuxiliaryProjection(harmony, AccessTools.DeclaredMethod(typeof(HextechRingOfTheDrakePlus), "ModifyHandDraw"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechInfusedCorePlus), "ModifyOrbValue"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechBlackBloodPlus), "AfterCombatVictory"));
        NativeCallbackContracts.Add(AccessTools.DeclaredMethod(typeof(HextechPhylacteryUnboundPlus), "BeforeCombatStart"));
        var flash = AccessTools.DeclaredMethod(typeof(OrobasPlusRelicBase), "Flash");
        var guard = AccessTools.DeclaredMethod(typeof(NativeRuneBridge), nameof(AllowDisplay));
        harmony.Patch(flash, prefix: new HarmonyMethod(guard));
        NativeCallbackContracts.AddNativePrefix(flash, guard, "HextechSolverCompat", Priority.Normal);
        RegisterNativeOrobasSideStart<HextechDivineDestinyPlus>(); RegisterNativeOrobasSideStart<HextechInfusedCorePlus>();
        RegisterNativeOrobasSideStart<HextechPhylacteryUnboundPlus>();
    }

    private static void RegisterNativeOrobasState<T>() where T : OrobasPlusRelicBase
    {
        ModelPredictionStateMirrors.RegisterRelic<T, NativeOrobasState>("native-orobas-readonly-v1",
            (_, live) => new(NativeOrobasState.Clone(live)), NativeOrobasState.WriteModel<T>, NativeOrobasState.Write);
        CompatibilityGuard.Runes.Add(typeof(T));
    }

    private static void RegisterNativeOrobasSideStart<T>() where T : OrobasPlusRelicBase
        => AuxiliarySideStart.Add(typeof(T), (relic, simulator, side, participants) =>
        {
            var model = (T)ModelPredictionStateMirrors.Get<NativeOrobasState>(simulator, relic).Model;
            var previous = _simulator; _simulator = simulator;
            try { RequireCompleted(model.AfterSideTurnStart(side, participants, simulator.State.CombatState), typeof(T)); }
            finally { _simulator = previous; }
        });

    private static Task ChannelNativeOrobasLightning(PlayerChoiceContext context, Player player)
        => _simulator is null ? OrbCmd.Channel<LightningOrb>(context, player)
            : ChannelOrb(context, ModelDb.Orb<LightningOrb>().ToMutable(), player);
}
