using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using HextechRunes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;

namespace HextechCompatLab;

internal static partial class FixtureAssertions
{
    private static async Task<UnattendedTestRunner.ExecutionOutcome> VerifyUnknownGuard(
        UnattendedTestRunner.ScenarioContext scenario, string kind)
    {
        if (kind == "relic")
        {
            // A real registered Hextech shop placeholder, never a selectable
            // player rune. Force it into inventory only in this negative probe.
            var relic = (RandomForgeShopRelic)ModelDb.GetById<RelicModel>(
                ModelDb.GetId(typeof(RandomForgeShopRelic))).ToMutable();
            await RelicCmd.Obtain(relic, scenario.Player);
            if (!scenario.Player.Relics.Contains(relic))
                throw new Exception("Unknown-relic probe did not obtain the actual Hextech model.");
            GD.Print("HEXTECH_UNKNOWN_GUARD_PREPARED official_model=RANDOM_FORGE_SHOP_RELIC inventory=true");
        }
        else if (kind == "enemy")
        {
            const int unknownValue = 2147483000;
            if (Enum.IsDefined(typeof(MonsterHexKind), unknownValue))
                throw new Exception("Unknown-enemy sentinel became a defined Hextech kind; review this probe.");
            var modifier = scenario.CombatState.Modifiers.OfType<HextechMayhemModifier>().Single();
            var hex = (MonsterHexKind)unknownValue;
            if (!modifier.DebugAddMonsterHex(hex) || !modifier.GetActiveMonsterHexes().Contains(hex))
                throw new Exception("Unknown-enemy probe did not enter the real active Hextech state.");
            GD.Print($"HEXTECH_UNKNOWN_GUARD_PREPARED unknown_enemy={unknownValue} active=true");
        }
        else throw new Exception("Unrecognized unknown-guard probe kind.");

        // Exercise the actual production capture/guard, without replacing it or
        // manufacturing the expected exception. A missing rejection must fail.
        _ = CombatRootSnapshot.Capture(scenario.CombatState);
        throw new Exception("Production root capture accepted unsupported Hextech content.");
    }
}
