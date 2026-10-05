using CombatSolver;
using HextechRunes;

namespace HextechSolverCompat;

// Only the counters/group state used by the reviewed native periodic/debuff
// handlers are exposed. Other tracking fields keep their defaults.
internal sealed class EnemyTurnTrackingState
{
    internal readonly HextechMayhemCombatTrackingState Model = new();
    internal EnemyTurnTrackingState(HextechMayhemCombatTrackingState source)
    {
        foreach (var item in source.GlobalProcsThisCombat) Model.GlobalProcsThisCombat.Add(item.Key, item.Value);
        foreach (var item in source.PlayerRuneProcsThisCombat) Model.PlayerRuneProcsThisCombat.Add(item.Key, item.Value);
        foreach (var item in source.TormentorProcsThisTurn) Model.TormentorProcsThisTurn.Add(item.Key, item.Value);
        Model.HandlingMonsterTormentorBurn = source.HandlingMonsterTormentorBurn;
        Model.HandlingServantMasterIllusion = source.HandlingServantMasterIllusion;
        Copy(source.InspectExtraDrawsPreventedThisTurn, Model.InspectExtraDrawsPreventedThisTurn);
        foreach (var pair in source.NearDeathFeastEnemyDebt) Model.NearDeathFeastEnemyDebt.Add(pair.Key, pair.Value);
        foreach (var pair in source.NearDeathFeastEnemyStrength) Model.NearDeathFeastEnemyStrength.Add(pair.Key, pair.Value);
        foreach (var pair in source.ShrinkEngineStacks) Model.ShrinkEngineStacks.Add(pair.Key, pair.Value);
        foreach (var pair in source.PlayerRuneProcsThisTurn) Model.PlayerRuneProcsThisTurn.Add(pair.Key, pair.Value);
        Model.PlayersAwaitingPlayPhase.UnionWith(source.PlayersAwaitingPlayPhase);
        Model.VakuuControlledPlayersThisCombat.UnionWith(source.VakuuControlledPlayersThisCombat);
        Model.EscapePlanTriggered.UnionWith(source.EscapePlanTriggered);
        Model.RepulsorTriggered.UnionWith(source.RepulsorTriggered);
        Model.DawnTriggered.UnionWith(source.DawnTriggered);
        Model.FeelTheBurnTriggered.UnionWith(source.FeelTheBurnTriggered);
        Model.FeelTheBurnPending.UnionWith(source.FeelTheBurnPending);
        foreach (var item in source.MikaelsBlessingTriggers) Model.MikaelsBlessingTriggers.Add(item.Key, item.Value);
        Copy(source.NightstalkingPlayerCardsDrawnThisCombat, Model.NightstalkingPlayerCardsDrawnThisCombat);
        Copy(source.WarmogsSpiritPlayerCardsDrawnThisCombat, Model.WarmogsSpiritPlayerCardsDrawnThisCombat);
        Copy(source.SwiftAndSafePlayerCardsDrawnThisCombat, Model.SwiftAndSafePlayerCardsDrawnThisCombat);
        Model.LastEnemyThresholdTriggerKey = source.LastEnemyThresholdTriggerKey;
        Model.MountainSoulHasPreviousTurn.UnionWith(source.MountainSoulHasPreviousTurn);
        Model.MountainSoulDamagedSinceLastTurn.UnionWith(source.MountainSoulDamagedSinceLastTurn);
        Model.SpeedDemonPending.UnionWith(source.SpeedDemonPending);
        Model.DevilsDanceTriggeredThisTurn.UnionWith(source.DevilsDanceTriggeredThisTurn);
        Model.GripPlayersTriggeredThisTurn.UnionWith(source.GripPlayersTriggeredThisTurn);
        Model.MindOverMatterPlayersTriggeredThisTurn.UnionWith(source.MindOverMatterPlayersTriggeredThisTurn);
        foreach (var pair in source.BloodArmorHpLossThisPlayerTurn) Model.BloodArmorHpLossThisPlayerTurn.Add(pair.Key, pair.Value);
        Copy(source.BackToBasicsCardsPlayedThisTurn, Model.BackToBasicsCardsPlayedThisTurn);
        Model.FinalFormTriggeredThisTurn.UnionWith(source.FinalFormTriggeredThisTurn);
        foreach (var pair in source.FeyMagicPendingNoDrawPlayers) Model.FeyMagicPendingNoDrawPlayers.Add(pair.Key, pair.Value);
        foreach (var pair in source.EnemyPorcupineUnblockedHitsThisCombat) Model.EnemyPorcupineUnblockedHitsThisCombat.Add(pair.Key, pair.Value);
        foreach (var pair in source.EnemyPorcupineTemporaryThornsThisTurn) Model.EnemyPorcupineTemporaryThornsThisTurn.Add(pair.Key, pair.Value);
        foreach (var pair in source.TankEngineStacks) Model.TankEngineStacks.Add(pair.Key, pair.Value);
        foreach (var pair in source.TankEngineLastAppliedRound) Model.TankEngineLastAppliedRound.Add(pair.Key, pair.Value);
        foreach (var pair in source.EnemyHundredRefinementsUnblockedHitsThisCombat)
            Model.EnemyHundredRefinementsUnblockedHitsThisCombat.Add(pair.Key, pair.Value);
    }
    private static void Copy(Dictionary<ulong, int> source, Dictionary<ulong, int> target)
    { foreach (var item in source) target.Add(item.Key, item.Value); }
    private static void WriteDrawCounts(ref ModelPredictionStateWriter writer, string name, Dictionary<ulong, int> values)
    {
        writer.Add(name + "Count", values.Count);
        foreach (var item in values.OrderBy(item => item.Key)) writer.Add(name + ":" + item.Key, item.Value);
    }
    private static void WriteSet(ref ModelPredictionStateWriter writer, string name, HashSet<uint> set)
    {
        writer.Add(name + "Count", set.Count);
        foreach (uint id in set.Order()) writer.Add(name + ":" + id, true);
    }
    internal void BindHp(EnemyHpState hp)
    {
        HarmonyLib.AccessTools.Field(typeof(HextechMayhemCombatTrackingState), nameof(Model.TankEngineStacks)).SetValue(Model, hp.LegacyTankStacks);
        HarmonyLib.AccessTools.Field(typeof(HextechMayhemCombatTrackingState), nameof(Model.MonsterMaxHpCoefficientBase)).SetValue(Model, hp.Base);
        HarmonyLib.AccessTools.Field(typeof(HextechMayhemCombatTrackingState), nameof(Model.MonsterMaxHpCoefficientProjected)).SetValue(Model, hp.Projected);
    }
    internal EnemyTurnTrackingState Fork() => new(Model);
    internal void Write(ref ModelPredictionStateWriter writer)
    {
        foreach (var (name, values) in new[] { ("NearDeathFeastEnemyDebt", Model.NearDeathFeastEnemyDebt), ("NearDeathFeastEnemyStrength", Model.NearDeathFeastEnemyStrength) })
        {
            writer.Add(name + "Count", values.Count);
            foreach (var pair in values.OrderBy(pair => pair.Key)) writer.Add(name + ":" + pair.Key, pair.Value);
        }
        writer.Add("ShrinkEngineStacksCount", Model.ShrinkEngineStacks.Count);
        foreach (var pair in Model.ShrinkEngineStacks.OrderBy(pair => pair.Key)) writer.Add("ShrinkEngineStacks:" + pair.Key, pair.Value);
        writer.Add("PlayerRuneProcsThisTurnCount", Model.PlayerRuneProcsThisTurn.Count);
        foreach (var pair in Model.PlayerRuneProcsThisTurn.OrderBy(pair => pair.Key, StringComparer.Ordinal)) writer.Add("PlayerRuneProcsThisTurn:" + pair.Key, pair.Value);
        WriteDrawCounts(ref writer, "InspectExtraDrawsPreventedThisTurn", Model.InspectExtraDrawsPreventedThisTurn);
        writer.Add("PlayersAwaitingPlayPhaseCount", Model.PlayersAwaitingPlayPhase.Count);
        foreach (ulong id in Model.PlayersAwaitingPlayPhase.Order()) writer.Add("PlayersAwaitingPlayPhase:" + id, true);
        writer.Add("VakuuControlledPlayersCount", Model.VakuuControlledPlayersThisCombat.Count);
        foreach (ulong id in Model.VakuuControlledPlayersThisCombat.Order()) writer.Add("VakuuControlledPlayers:" + id, true);
        WriteSet(ref writer, "FinalFormTriggeredThisTurn", Model.FinalFormTriggeredThisTurn);
        WriteDrawCounts(ref writer, "BackToBasicsCardsPlayedThisTurn", Model.BackToBasicsCardsPlayedThisTurn);
        foreach (var (name, values) in new[] { ("BloodArmorHpLossThisPlayerTurn", Model.BloodArmorHpLossThisPlayerTurn), ("EnemyPorcupineUnblockedHitsThisCombat", Model.EnemyPorcupineUnblockedHitsThisCombat), ("EnemyPorcupineTemporaryThornsThisTurn", Model.EnemyPorcupineTemporaryThornsThisTurn) })
        {
            writer.Add(name + "Count", values.Count);
            foreach (var pair in values.OrderBy(pair => pair.Key)) writer.Add(name + ":" + pair.Key, pair.Value);
        }
        writer.Add("FeyMagicPendingNoDrawPlayersCount", Model.FeyMagicPendingNoDrawPlayers.Count);
        foreach (var pair in Model.FeyMagicPendingNoDrawPlayers.OrderBy(pair => pair.Key)) writer.Add("FeyMagicPendingNoDrawPlayers:" + pair.Key, pair.Value);

        WriteSet(ref writer, "nativeEscapePlan", Model.EscapePlanTriggered);
        WriteSet(ref writer, "nativeRepulsor", Model.RepulsorTriggered);
        WriteSet(ref writer, "nativeDawn", Model.DawnTriggered);
        WriteSet(ref writer, "nativeFeelBurn", Model.FeelTheBurnTriggered);
        WriteSet(ref writer, "nativeFeelBurnPending", Model.FeelTheBurnPending);
        writer.Add("nativeTankRoundsCount", Model.TankEngineLastAppliedRound.Count);
        foreach (var pair in Model.TankEngineLastAppliedRound.OrderBy(pair => pair.Key)) writer.Add("nativeTankRound:" + pair.Key, pair.Value);
        writer.Add("nativeMindPlayersCount", Model.MindOverMatterPlayersTriggeredThisTurn.Count);
        foreach (ulong id in Model.MindOverMatterPlayersTriggeredThisTurn.Order()) writer.Add("nativeMindPlayer:" + id, true);
        writer.Add("nativeHundredHitsCount", Model.EnemyHundredRefinementsUnblockedHitsThisCombat.Count);
        foreach (var pair in Model.EnemyHundredRefinementsUnblockedHitsThisCombat.OrderBy(pair => pair.Key))
            writer.Add("nativeHundredHits:" + pair.Key, pair.Value);
        writer.Add("nativeGripPlayersCount", Model.GripPlayersTriggeredThisTurn.Count);
        foreach (ulong id in Model.GripPlayersTriggeredThisTurn.Order()) writer.Add("nativeGripPlayer:" + id, true);
        WriteSet(ref writer, "nativeMountainPrevious", Model.MountainSoulHasPreviousTurn);
        WriteSet(ref writer, "nativeMountainDamaged", Model.MountainSoulDamagedSinceLastTurn);
        WriteSet(ref writer, "nativeSpeedDemonPending", Model.SpeedDemonPending);
        WriteSet(ref writer, "nativeDevilsDanceTriggered", Model.DevilsDanceTriggeredThisTurn);
        writer.Add("nativeMikaelCount", Model.MikaelsBlessingTriggers.Count);
        foreach (var item in Model.MikaelsBlessingTriggers.OrderBy(item => item.Key)) writer.Add("nativeMikael:" + item.Key, item.Value);
        writer.Add("nativeLastThresholdKey", Model.LastEnemyThresholdTriggerKey ?? "");
        WriteDrawCounts(ref writer, "nativeNightDraw", Model.NightstalkingPlayerCardsDrawnThisCombat);
        WriteDrawCounts(ref writer, "nativeWarmogsDraw", Model.WarmogsSpiritPlayerCardsDrawnThisCombat);
        WriteDrawCounts(ref writer, "nativeSwiftDraw", Model.SwiftAndSafePlayerCardsDrawnThisCombat);
        writer.Add("nativeTormentorHandling", Model.HandlingMonsterTormentorBurn);
        writer.Add("nativeServantHandling", Model.HandlingServantMasterIllusion);
        writer.Add("nativeTormentorCount", Model.TormentorProcsThisTurn.Count);
        foreach (var item in Model.TormentorProcsThisTurn.OrderBy(item => item.Key))
            writer.Add("nativeTormentor:" + item.Key, item.Value);
        writer.Add("nativeGlobalProcCount", Model.GlobalProcsThisCombat.Count);
        foreach (var item in Model.GlobalProcsThisCombat.OrderBy(item => item.Key, StringComparer.Ordinal))
            writer.Add("nativeGlobalProc:" + item.Key, item.Value);
        writer.Add("nativeCombatProcCount", Model.PlayerRuneProcsThisCombat.Count);
        foreach (var item in Model.PlayerRuneProcsThisCombat.OrderBy(item => item.Key, StringComparer.Ordinal))
            writer.Add("nativeCombatProc:" + item.Key, item.Value);
    }
}
