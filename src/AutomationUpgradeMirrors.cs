using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using HextechRunes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using System.Text;

namespace HextechSolverCompat;

internal static class AutomationUpgradeMirrors
{
    internal static void Register(Harmony harmony)
    {
        RuneMirrors.RegisterNativeBase<AutomationUpgradeRune>();
        harmony.Patch(AccessTools.Method(typeof(AfterCardDrawnMirrors), "HandleAutomationPower"),
            prefix: new HarmonyMethod(typeof(AutomationUpgradeMirrors), nameof(AfterDraw)));
        // The pinned solver fingerprints this counter but its continuation
        // writer omits it. Both native and predicted stamps need the same field.
        harmony.Patch(AccessTools.Method(typeof(ContinuationStamp), "AppendPowers"),
            postfix: new HarmonyMethod(typeof(AutomationUpgradeMirrors), nameof(AppendCounter)));
    }

    private static void AppendCounter(StringBuilder text, IEnumerable<PowerModel> powers,
        CombatPredictionSimulator? simulator)
    {
        foreach (var power in powers.OfType<AutomationPower>().Where(power => power.Amount > 0))
        {
            int count = simulator is null ? power.DisplayAmount
                : simulator.StateStore.Peek(power, static power => new AutomationPredictionState(power)).CardsLeft;
            text.Append(";hextech_automation_counter=").Append(power.Owner.CombatId).Append(':').Append(count);
        }
    }

    private static bool AfterDraw(AutomationPower power, AfterCardDrawnMirrorContext context)
    {
        var player = power.Owner.Player;
        var combat = (SimulatedCombatState)context.State.CombatState;
        var rune = player is null ? null : combat.RelicsOf(player).OfType<AutomationUpgradeRune>().SingleOrDefault();
        if (rune is null) return true;
        if (context.PreviewCard.Owner != player) return false;
        var counter = context.StateStore.Get(power, () => new AutomationPredictionState(power));
        counter.CardsLeft = Math.Max(0, counter.CardsLeft - 1);
        if (counter.CardsLeft > 0) return false;
        // The official replacement refunds, resets, then draws. Nested draws
        // must observe the reset, not the counter which just reached zero.
        context.Simulator.GainEnergy(player!, power.Amount);
        counter.CardsLeft = 10;
        if (context.State.GetCreature(player!.Creature).IsAlive)
            context.Simulator.Draw(player, rune.DynamicVars.Cards.BaseValue, fromHandDraw: false);
        context.Simulator.AcknowledgeExecutionDispatch();
        return false;
    }
}
