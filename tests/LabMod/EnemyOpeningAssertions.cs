using CombatSolver;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyEnemyOpening(
        UnattendedTestRunner runner, UnattendedTestRunner.ScenarioContext scenario, string mode)
    {
        var combat = scenario.CombatState;
        var player = scenario.Player;
        var enemy = combat.Enemies.Single();
        var modifier = combat.Modifiers.OfType<HextechMayhemModifier>().Single();
        var kind = Enum.Parse<MonsterHexKind>(mode);
        if (!modifier.HasActiveMonsterHex(kind)) throw new Exception("Opening fixture did not actually select its enemy hex.");
        int tier = modifier.GetMonsterHexStrengthTier(kind);
        int expected, actual;
        switch (kind)
        {
            case MonsterHexKind.ProtectiveVeil:
                expected = tier; actual = enemy.GetPowerAmount<ArtifactPower>(); break;
            case MonsterHexKind.Thornmail:
                expected = tier - 1; actual = enemy.GetPowerAmount<ThornsPower>(); break;
            case MonsterHexKind.StartupRoutine:
                expected = tier <= 1 ? 10 : tier == 2 ? 15 : 20; actual = enemy.Block; break;
            case MonsterHexKind.BrutalForce:
                if (enemy.GetPowerAmount<StrengthPower>() != 1) throw new Exception("BrutalForce opening Strength was not applied.");
                expected = Math.Max(1, (int)Math.Floor(enemy.MaxHp * BrutalForceEnemyHex.BlockPercent));
                actual = enemy.Block; break;
            case MonsterHexKind.Zealot:
                expected = tier; actual = enemy.GetPowerAmount<VigorPower>(); break;
            case MonsterHexKind.SuperBrain:
                expected = (int)Math.Floor(enemy.MaxHp * (tier <= 1 ? .03m : tier == 2 ? .04m : .05m));
                actual = enemy.GetPowerAmount<PlatingPower>(); break;
            case MonsterHexKind.SkulkingColony:
                expected = Math.Max(12, (int)Math.Floor(enemy.MaxHp * .6m)); actual = enemy.GetPowerAmount<HardenedShellPower>(); break;
            case MonsterHexKind.PhantasmalGardener:
                expected = Math.Max(1, (int)Math.Floor(enemy.MaxHp * (tier <= 1 ? .08m : tier == 2 ? .10m : .12m)));
                actual = enemy.GetPowerAmount<SkittishPower>(); break;
            case MonsterHexKind.Exoskeleton:
                expected = ExoskeletonEnemyHex.ResolveHardToKill(enemy.MaxHp, tier); actual = enemy.GetPowerAmount<HardToKillPower>(); break;
            case MonsterHexKind.ShrinkerBeetle:
                expected = tier; actual = player.Creature.GetPowerAmount<ShrinkPower>(); break;
            case MonsterHexKind.TheLost:
                expected = -(tier - 1); actual = player.Creature.GetPowerAmount<StrengthPower>(); break;
            case MonsterHexKind.TheForgotten:
                expected = -(tier - 1); actual = player.Creature.GetPowerAmount<DexterityPower>(); break;
            case MonsterHexKind.Inklet:
                expected = tier; actual = enemy.GetPowerAmount<SlipperyPower>(); break;
            case MonsterHexKind.Vantom:
                expected = (int)Math.Floor(enemy.MaxHp / VantomEnemyHex.MaxHpPerStack); actual = enemy.GetPowerAmount<SlipperyPower>(); break;
            case MonsterHexKind.FossilStalker:
                expected = tier; actual = enemy.GetPowerAmount<SuckPower>(); break;
            case MonsterHexKind.Byrdonis:
                expected = tier - 1; actual = enemy.GetPowerAmount<TerritorialPower>(); break;
            default: throw new Exception("Unreviewed opening enemy hex probe.");
        }
        if (actual != expected) throw new Exception($"Opening {kind} tier={tier} expected={expected}, actual={actual}, enemy_max_hp={enemy.MaxHp}.");
        for (int i = 0; i < 2; i++) await runner.AssertReportRoundAsync(combat, player);
        GD.Print($"HEXTECH_ENEMY_OPENING_VERIFIED kind={kind} tier={tier} opening={actual} native_two_rounds=true");
        return new(false, player.PlayerCombatState!.TurnNumber, true, false, false, false);
    }
}
