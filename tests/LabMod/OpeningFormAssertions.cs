using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static void VerifyOpeningFormRoot(UnattendedTestRunner.ScenarioContext scenario, JsonElement probe)
    {
        var player = scenario.Player;
        var live = scenario.CombatState;
        string runeId = probe.GetProperty("rune").GetString()!;
        string cardId = probe.GetProperty("card").GetString()!;
        var rune = player.Relics.Single(rune => rune.Id.Entry == runeId);
        var started = AccessTools.Field(rune.GetType(), "_startedThisCombat");
        var playing = AccessTools.Field(rune.GetType(), "_autoPlaying");
        GD.Print($"HEXTECH_OPENING_FORM_STATE rune={runeId} started={started.GetValue(rune)} playing={playing.GetValue(rune)} turn={player.PlayerCombatState!.TurnNumber} phase={player.PlayerCombatState.Phase}");
        if (probe.TryGetProperty("inactive", out var inactive) && inactive.GetBoolean())
        {
            bool available = (bool)AccessTools.DeclaredMethod(rune.GetType(), "IsAvailableForCharacter").Invoke(rune, [player])!;
            if (available || (bool)started.GetValue(rune)! || (bool)playing.GetValue(rune)!
                || MegaCrit.Sts2.Core.Combat.CombatManager.Instance.History.CardPlaysStarted.Any(
                    entry => entry.CardPlay.Card.Id.Entry == cardId && entry.CardPlay.IsAutoPlay))
                throw new Exception("Inactive opening form unexpectedly ran or was marked completed.");
            _ = CombatRootSnapshot.Capture(live);
            GD.Print($"HEXTECH_OPENING_FORM_ROOT_VERIFIED rune={runeId} native_autoplays=0 original_inactive_predicate=true no_opening_replayed=true");
            return;
        }
        if (!(bool)started.GetValue(rune)! || (bool)playing.GetValue(rune)!
            || player.PlayerCombatState!.TurnNumber != 1 || player.PlayerCombatState.Phase != PlayerTurnPhase.Play)
            throw new Exception("Original opening form batch did not finish on the first player turn.");
        int plays = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.History.CardPlaysStarted.Count(
            entry => entry.CardPlay.Card.Id.Entry == cardId && entry.CardPlay.IsAutoPlay);
        if (plays < probe.GetProperty("minimumNativePlays").GetInt32())
            throw new Exception("Opening form fixture did not exercise the original auto-play batch.");
        if (probe.TryGetProperty("initialPower", out var power))
        {
            int actual = Observe(scenario, "power:" + power.GetProperty("id").GetString());
            if (actual != power.GetProperty("amount").GetInt32())
                throw new Exception($"Native opening form amount differs: {actual}.");
        }
        string stamp = ContinuationStamp.CaptureLive(live).StateText;
        _ = CombatRootSnapshot.Capture(live);
        try
        {
            started.SetValue(rune, false);
            if (ContinuationStamp.CaptureLive(live).StateText == stamp)
                throw new Exception("Unsettled opening form left an old route valid.");
            try
            {
                _ = CombatRootSnapshot.Capture(live);
                throw new Exception("Opening form root was captured before its native batch.");
            }
            catch (PredictionUnsupportedException error) when (error.Message.Contains("开局形态仍在原生结算")) { }
        }
        finally { started.SetValue(rune, true); }
        if (ContinuationStamp.CaptureLive(live).StateText != stamp)
            throw new Exception("Opening form boundary probe changed the actual combat.");
        GD.Print($"HEXTECH_OPENING_FORM_ROOT_VERIFIED rune={runeId} native_autoplays={plays} first_turn=1 original_batch=true unsettled_rejected=true live_restored=true");
    }
}
