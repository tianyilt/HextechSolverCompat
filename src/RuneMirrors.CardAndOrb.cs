using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;

namespace HextechSolverCompat;

internal static partial class RuneMirrors
{
    private static void RegisterCardAndOrbEffects()
    {
        RegisterBase<UltimateUnstoppableRune>();
        AfterCardPlayedMirrors.Registry.Register<UltimateUnstoppableRune>((r, c) =>
        {
            if (c.Card.Preview.Owner == r.Owner && c.CardPlay.Resources.EnergyValue >= r.DynamicVars["MinCost"].BaseValue)
                ((SimulatedCombatState)c.CombatState).ApplyPowerFromSource(typeof(ArtifactPower), r.Owner.Creature,
                    r.DynamicVars["ArtifactPower"].IntValue, r.Owner.Creature, c.Card.Preview);
        });
        RegisterBase<CantTouchThisRune>();
        AfterCardPlayedMirrors.Registry.Register<CantTouchThisRune>((r, c) =>
        {
            if (c.Card.Preview.Owner == r.Owner && c.CardPlay.Resources.EnergyValue >= r.DynamicVars["MinCost"].BaseValue)
                ((SimulatedCombatState)c.CombatState).ApplyPowerFromSource(typeof(BufferPower), r.Owner.Creature,
                    r.DynamicVars["BufferPower"].IntValue, r.Owner.Creature, null);
        });
        RegisterBase<FlawlessRune>();
        AfterCardPlayedMirrors.Registry.Register<FlawlessRune>((r, c) =>
        {
            if (c.Card.Preview.Owner == r.Owner && c.State.GetCreature(r.Owner.Creature).IsAlive &&
                HextechColorlessCardHelper.IsColorlessCard(c.Card.Preview))
                c.Simulator.GainBlock(r.Owner.Creature, r.DynamicVars.Block, null, c.CardPlay);
        });
        // MasterOfDuality uses its original callback and shared native stat bridge.
        RegisterBase<LethalTempoRune>();
        AfterCardPlayedMirrors.Registry.Register<LethalTempoRune>((r, c) =>
        {
            if (c.Card.Preview.Owner != r.Owner || !c.State.GetCreature(r.Owner.Creature).IsAlive) return;
            var combat = (SimulatedCombatState)c.CombatState;
            if (!c.Card.Preview.Tags.Contains(CardTag.Shiv) &&
                !(c.Card.Preview is SovereignBlade && combat.RelicsOf(r.Owner).OfType<BigKnifeRune>().Any())) return;
            combat.BeginCardPowerApplication(c.Card.Preview);
            try
            {
                combat.ApplyTemporaryStrengthGain<HextechLethalTempoTemporaryStrengthPower>(r.Owner.Creature,
                    r.DynamicVars.Strength.IntValue, r.Owner.Creature);
            }
            finally { combat.CompleteCardPowerApplication(c.Card.Preview); }
        });

        RegisterBase<ElectricSurgeRune>();
        AfterPlayerTurnStartMirrors.Register<ElectricSurgeRune>((r, c) =>
        {
            if (c.Player == r.Owner && c.State.GetCreature(r.Owner.Creature).IsAlive)
                c.Simulator.OrbChannel<LightningOrb>(r.Owner, r.DynamicVars["OrbCount"].IntValue);
        });
        RegisterBase<GloomyCloudsRune>();
        AfterPlayerTurnStartMirrors.Register<GloomyCloudsRune>((r, c) =>
        {
            if (c.Player != r.Owner || !c.State.GetCreature(r.Owner.Creature).IsAlive) return;
            foreach (var orb in c.State.GetPlayerCombatState(r.Owner).OrbQueue.Orbs.OfType<DarkOrb>().ToArray())
                c.Simulator.OrbPassive(orb);
        });
        RegisterBase<ImmortalBoneRune>();
        AfterPlayerTurnStartMirrors.Register<ImmortalBoneRune>((r, c) =>
        {
            if (c.Player != r.Owner || !c.State.GetCreature(r.Owner.Creature).IsAlive ||
                ((SimulatedCombatState)c.CombatState).GetOsty(r.Owner) is not { } osty) return;
            var target = c.State.GetCreature(osty);
            if (target.IsAlive)
                c.Simulator.Heal(osty, PercentHeal(target.MaxHp, r.DynamicVars["HealPercent"].BaseValue));
        });
    }

    private static bool IsSkill(CardModel card) => (card.CanonicalInstance?.Type ?? card.Type) == CardType.Skill;
    private static bool IsAttack(CardModel card, SimulatedCombatState combat) => card.Type == CardType.Attack ||
        (IsSkill(card) && combat.RelicsOf(card.Owner).OfType<IllusoryWeaponRune>().Any());
}
