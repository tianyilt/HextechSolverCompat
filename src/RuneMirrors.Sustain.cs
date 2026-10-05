using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using HextechRunes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace HextechSolverCompat;

internal sealed class MountainSoulState(bool damaged, bool previousTurn) : IPredictionStateForkable
{
    internal bool Damaged = damaged;
    internal bool PreviousTurn = previousTurn;
    internal static MountainSoulState Capture(MountainSoulRune rune) =>
        new(rune.SavedTookUnblockedDamageSinceLastTurn, rune.SavedHasPreviousTurn);
    public object Fork(PredictionForkContext context) => new MountainSoulState(Damaged, PreviousTurn);
    internal static void Write(MountainSoulState state, ref ModelPredictionStateWriter writer)
    {
        writer.Add("damaged", state.Damaged);
        writer.Add("previousTurn", state.PreviousTurn);
    }
}

internal static partial class RuneMirrors
{
    private static void RegisterSustain()
    {
        RegisterBase<SturdyRune>();
        AfterPlayerTurnStartMirrors.Register<SturdyRune>((r, c) =>
        {
            if (c.Player != r.Owner) return;
            var target = c.State.GetCreature(r.Owner.Creature);
            // Player Sturdy uses 5% below half HP. Enemy Sturdy uses 4%.
            decimal percent = target.CurrentHp * 100m < target.MaxHp * r.DynamicVars["LowHpThresholdPercent"].BaseValue
                ? r.DynamicVars["LowHpHealPercent"].BaseValue : r.DynamicVars["HealPercent"].BaseValue;
            c.Simulator.Heal(r.Owner.Creature, PercentHeal(target.MaxHp, percent));
        });

        RegisterBase<AncientWineRune>();
        AfterCardPlayedMirrors.Registry.Register<AncientWineRune>((r, c) =>
        {
            if (c.Card.Preview.Owner != r.Owner || !IsSkill(c.Card.Preview)) return;
            var target = c.State.GetCreature(r.Owner.Creature);
            if (target.IsAlive)
                c.Simulator.Heal(r.Owner.Creature, PercentHeal(target.MaxHp, r.DynamicVars["HealPercent"].BaseValue));
        });

        RegisterBase<MountainSoulRune>();
        ModelPredictionStateMirrors.RegisterRelic<MountainSoulRune, MountainSoulState>(
            "mountain-soul-turn-v1", (_, r) => MountainSoulState.Capture(r),
            (MountainSoulRune r, ref ModelPredictionStateWriter w) => MountainSoulState.Write(MountainSoulState.Capture(r), ref w),
            MountainSoulState.Write);
        AfterDamageReceivedMirrors.Registry.Register<MountainSoulRune>((r, c) =>
        {
            if (c.Target == r.Owner.Creature && c.Result.UnblockedDamage > 0)
                ModelPredictionStateMirrors.Get<MountainSoulState>(c.Simulator, r).Damaged = true;
        });
        AfterPlayerTurnStartMirrors.Register<MountainSoulRune>((r, c) =>
        {
            if (c.Player != r.Owner) return;
            var state = ModelPredictionStateMirrors.Get<MountainSoulState>(c.Simulator, r);
            if (state.PreviousTurn && !state.Damaged)
                c.Simulator.GainBlock(r.Owner.Creature,
                    Math.Max(1m, Math.Floor(c.State.GetCreature(r.Owner.Creature).MaxHp * .1m)), ValueProp.Unpowered);
            state.PreviousTurn = true;
            state.Damaged = false;
        });

        RegisterBase<TanksShieldRune>();
        AfterCardPlayedMirrors.Registry.Register<TanksShieldRune>((r, c) =>
        {
            if (c.Card.Preview.Owner == r.Owner && IsAttack(c.Card.Preview, (SimulatedCombatState)c.CombatState) &&
                c.State.GetCreature(r.Owner.Creature).IsAlive)
                c.Simulator.GainBlock(r.Owner.Creature, r.DynamicVars.Block, null, c.CardPlay);
        });

        RegisterBase<PrecisionCognitionRune>();
        AfterPlayerTurnStartMirrors.Register<PrecisionCognitionRune>((r, c) =>
        {
            if (c.Player == r.Owner && c.State.GetCreature(r.Owner.Creature).IsAlive)
                ((SimulatedCombatState)c.CombatState).ApplyPowerFromSource(typeof(FocusPower),
                    r.Owner.Creature, r.DynamicVars["FocusPower"].IntValue, r.Owner.Creature, null);
        });

        RegisterBase<StarlightSplendorRune>();
        AfterPlayerTurnStartMirrors.Register<StarlightSplendorRune>((r, c) =>
        {
            if (c.Player != r.Owner || !c.State.GetCreature(r.Owner.Creature).IsAlive) return;
            decimal stars = c.CombatState.RoundNumber * r.DynamicVars.Stars.BaseValue;
            if (stars > 0) c.Simulator.GainStars(r.Owner, stars);
        });

        RegisterBase<SomethingFromNothingRune>();
        AfterCardPlayedMirrors.Registry.Register<SomethingFromNothingRune>((r, c) =>
        {
            if (c.Card.Preview.Owner == r.Owner && c.Card.Preview.Keywords.Contains(CardKeyword.Ethereal))
                c.Simulator.Draw(r.Owner, r.DynamicVars.Cards.BaseValue, fromHandDraw: false);
        });
    }

    private static decimal PercentHeal(int maxHp, decimal percent) =>
        Math.Max(1m, Math.Floor(Math.Max(0, maxHp) * Math.Max(0m, percent) / 100m));
}
