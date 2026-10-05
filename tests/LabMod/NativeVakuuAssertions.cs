using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HarmonyLib;
using HextechRunes;
using MegaCrit.Sts2.Core.Models;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static bool _vakuuObserverInstalled;
    private static readonly List<string> OwnedVakuuPayments = [];
    private static readonly List<string> NativeVakuuPayments = [];
    private static bool _traceVakuuPayments;
    private static int NativeVakuuRoundDeadlineSeconds()
    {
        using var request = Request();
        return request.RootElement.TryGetProperty("hextechNativeVakuuProbe", out var flag) && flag.GetBoolean()
            && request.RootElement.GetProperty("hextechNativeVakuuMinimumPayments").GetInt32() == 13 ? 40 : 20;
    }
    private static void BeginNativeVakuuRound()
    {
        using var request = Request();
        _traceVakuuPayments = request.RootElement.TryGetProperty("hextechNativeVakuuProbe", out var flag) && flag.GetBoolean();
        if (!_traceVakuuPayments) return;
        OwnedVakuuPayments.Clear(); NativeVakuuPayments.Clear();
        if (_vakuuObserverInstalled) return;
        new Harmony("HextechCompatLab.VakuuPaymentObserver").Patch(
            AccessTools.DeclaredMethod(AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge"), "SpendNativeVakuuResources"),
            prefix: new HarmonyMethod(typeof(FixtureAssertions), nameof(ObserveNativeVakuuPayment)));
        _vakuuObserverInstalled = true;
    }
    private static void ObserveNativeVakuuPayment(CardModel card)
    {
        if (!_traceVakuuPayments) return;
        bool owned = AccessTools.Field(AccessTools.TypeByName("HextechSolverCompat.NativeRuneBridge"), "_simulator").GetValue(null)
            is CombatPredictionSimulator;
        (owned ? OwnedVakuuPayments : NativeVakuuPayments).Add(card.Id.Entry + "+" + card.CurrentUpgradeLevel);
        if (!owned) GD.Print($"HEXTECH_NATIVE_VAKUU_PAYMENT count={NativeVakuuPayments.Count} card={card.Id.Entry}");
    }
    private static void CompleteNativeVakuuRound(UnattendedTestRunner.ScenarioContext scenario)
    {
        if (!_traceVakuuPayments) return;
        _traceVakuuPayments = false;
        using var request = Request();
        int round = scenario.CombatState.RoundNumber;
        int count = NativeVakuuPayments.Count;
        if (round == 2)
        {
            int minimum = request.RootElement.GetProperty("hextechNativeVakuuMinimumPayments").GetInt32();
            if (count < minimum || count > 13 || OwnedVakuuPayments.Count < count
                || count == 0 && OwnedVakuuPayments.Count != 0)
                throw new Exception($"Vakuu original controlled action count was not exercised: native={count} owned={OwnedVakuuPayments.Count} minimum={minimum}.");
            if (count > 0)
            {
                if (OwnedVakuuPayments.Count % count != 0)
                    throw new Exception("Vakuu branch payment replay did not complete a whole original action sequence.");
                for (int start = 0; start < OwnedVakuuPayments.Count; start += count)
                    if (!OwnedVakuuPayments.Skip(start).Take(count).SequenceEqual(NativeVakuuPayments))
                        throw new Exception("Vakuu original playable-hand payment order differed from prediction.");
            }
            var modifier = scenario.CombatState.Modifiers.OfType<HextechMayhemModifier>().Single();
            if (!modifier.CombatTracking.VakuuControlledPlayersThisCombat.Contains(scenario.Player.NetId))
                throw new Exception("Vakuu original once-per-combat controller marker was not recorded.");
        }
        else if (count != 0 || OwnedVakuuPayments.Count != 0)
            throw new Exception("Enemy Vakuu controlled a round other than round two.");
        GD.Print($"HEXTECH_NATIVE_VAKUU_VERIFIED round={round} native_payments={count} original_payment_order=true max13=true native_tracking=true future_round_snapshots_required=true");
    }
}
