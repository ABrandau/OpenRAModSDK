#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.SP.Traits.BotModules.Squads
{
	abstract class GroundStateBaseSP : StateBaseSP
	{
		protected virtual bool ShouldFlee(SquadSP owner)
		{
			return ShouldFlee(owner, enemies => !AttackOrFleeFuzzySP.Default.CanAttack(owner.Units, enemies));
		}

		protected Actor FindClosestEnemy(SquadSP owner)
		{
			return owner.SquadManager.FindClosestEnemy(owner.Units.First().CenterPosition);
		}

		protected Actor GetRandomValuableTarget(SquadSP owner)
		{
			var manager = owner.SquadManager;
			var mustDestroyedEnemy = manager.World.ActorsHavingTrait<MustBeDestroyed>(t => t.Info.RequiredForShortGame)
					.Where(a => manager.IsPreferredEnemyUnit(a) && manager.IsNotHiddenUnit(a)).ToArray();

			if (!mustDestroyedEnemy.Any())
				return FindClosestEnemy(owner);

			return mustDestroyedEnemy.Random(owner.World.LocalRandom);
		}

		protected Actor ThreatScan(SquadSP owner, Actor teamLeader, WDist scanRadius)
		{
			var enemies = owner.World.FindActorsInCircle(teamLeader.CenterPosition, scanRadius)
					.Where(a => owner.SquadManager.IsPreferredEnemyUnit(a) && owner.SquadManager.IsNotHiddenUnit(a));
			return enemies.ClosestTo(teamLeader.CenterPosition);
		}
	}

	class GroundUnitsIdleStateSP : GroundStateBaseSP, IState
	{
		public void Activate(SquadSP owner) { }

		public void Tick(SquadSP owner)
		{
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				var closestEnemy = FindClosestEnemy(owner);
				if (closestEnemy == null)
					return;

				owner.TargetActor = closestEnemy;
			}

			var enemyUnits = owner.World.FindActorsInCircle(owner.TargetActor.CenterPosition, WDist.FromCells(owner.SquadManager.Info.IdleScanRadius))
				.Where(a => owner.SquadManager.IsPreferredEnemyUnit(a) && owner.SquadManager.IsNotHiddenUnit(a)).ToList();

			if (enemyUnits.Count == 0)
			{
				Retreat(owner, false, true, true);
				return;
			}

			if (AttackOrFleeFuzzySP.Default.CanAttack(owner.Units, enemyUnits))
			{
				// We have gathered sufficient units. Attack the nearest enemy unit.
				// Inform human allies about AI's rush attack.
				owner.Bot.QueueOrder(new Order("PlaceBeacon", owner.SquadManager.Player.PlayerActor, Target.FromCell(owner.World, owner.TargetActor.Location), false)
				{ SuppressVisualFeedback = true });
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsAttackMoveStateSP(), true);
			}
			else
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsFleeStateSP(), true);
		}

		public void Deactivate(SquadSP owner) { }
	}

	class GroundUnitsAttackMoveStateSP : GroundStateBaseSP, IState
	{
		public const int StuckInPathCheckTimes = 6;
		public const int MakeWayTick = 2;

		// Give tolerance for AI grouping team at start
		internal int StuckInPath = StuckInPathCheckTimes * 3;
		internal int TryMakeWay = MakeWayTick;
		internal WPos LastPos = new WPos(0, 0, 0);

		public void Activate(SquadSP owner) { }

		public void Tick(SquadSP owner)
		{
			// Basic check
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				var randomSuitableEnemy = GetRandomValuableTarget(owner);
				if (randomSuitableEnemy != null)
					owner.TargetActor = randomSuitableEnemy;
				else
				{
					owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsFleeStateSP(), true);
					return;
				}
			}

			// Initialize pathGuider. Optimaze pathfinding by using pathGuider.
			var pathGuider = owner.Units.FirstOrDefault();
			if (pathGuider == null)
				return;

			// 1. Threat scan surroundings
			var attackScanRadius = WDist.FromCells(owner.SquadManager.Info.AttackScanRadius);

			var targetActor = ThreatScan(owner, pathGuider, attackScanRadius);
			if (targetActor != null)
			{
				owner.TargetActor = targetActor;
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsAttackStateSP(), true);
				return;
			}

			// 2. Force scattered for pathGuider if needed
			if (StuckInPath <= 0)
			{
				if (TryMakeWay > 0)
				{
					owner.Bot.QueueOrder(new Order("AttackMove", pathGuider, Target.FromCell(owner.World, owner.TargetActor.Location), false));

					foreach (var a in owner.Units)
					{
						if (a != pathGuider)
							owner.Bot.QueueOrder(new Order("Scatter", a, false));
					}

					TryMakeWay--;
				}
				else
				{
					// When going through is over, restore the check
					StuckInPath = StuckInPathCheckTimes + MakeWayTick;
					TryMakeWay = MakeWayTick;
				}

				return;
			}

			// 3. Check if the squad is stucked due to the map has very twisted path
			// or currently bridge and tunnel from TS mod
			if ((pathGuider.CenterPosition - LastPos).LengthSquared <= 4)
				StuckInPath--;
			else
				StuckInPath = StuckInPathCheckTimes;

			LastPos = pathGuider.CenterPosition;

			// 4. Since units have different movement speeds, they get separated while approaching the target.

			/* Let them regroup into tighter formation towards "pathGuider".
			 *
			 * "unitsArea" means the space the squad units will occupy (if 1 per Cell).
			 * pathGuider only stop when scope of "unitsAround" is not covered all units;
			 * units in "unitsHurryUp"  will catch up,
			 * which keep the team tight while not stucked.
			 *
			 * Imagining "unitsArea" takes up a a place shape like square, we need to draw a circle
			 * to cover the the enitire circle.
			 *
			 * When look around, pathGuider find units around to decide if it need to continue.
			 * and units that need hurry up will try catch up before guider waiting
			 *
			 * However in practice because of the poor PF, squad tend to PF to a eclipse.
			 * "lookAround" now has the radius of two times of the circle mentioned before.
			 */

			var groupArea = (long)WDist.FromCells(owner.Units.Count).Length * 1024;

			var unitsHurryUp = owner.Units.Where(a => (a.CenterPosition - pathGuider.CenterPosition).LengthSquared >= groupArea * 2).ToArray();
			var lookAround = owner.Units.Where(a => (a.CenterPosition - pathGuider.CenterPosition).LengthSquared <= groupArea * 5).ToArray();

			if (owner.Units.Count > lookAround.Length)
				owner.Bot.QueueOrder(new Order("Stop", pathGuider, false));
			else
				owner.Bot.QueueOrder(new Order("AttackMove", pathGuider, Target.FromCell(owner.World, owner.TargetActor.Location), false));

			foreach (var unit in unitsHurryUp)
				owner.Bot.QueueOrder(new Order("AttackMove", unit, Target.FromCell(owner.World, pathGuider.Location), false));
		}

		public void Deactivate(SquadSP owner) { }
	}

	class GroundUnitsAttackStateSP : GroundStateBaseSP, IState
	{
		public void Activate(SquadSP owner) { }

		public void Tick(SquadSP owner)
		{
			var cannotRetaliate = false;

			// Basic check
			if (!owner.IsValid)
				return;

			var teamLeader = owner.Units.FirstOrDefault();
			if (teamLeader == null)
				return;

			// Rescan target to prevent being ambushed and die without fight
			// If there is no threat around, return to AttackMove state for formation
			var attackScanRadius = WDist.FromCells(owner.SquadManager.Info.AttackScanRadius);
			var targetActor = ThreatScan(owner, teamLeader, attackScanRadius);

			if (targetActor == null)
			{
				owner.TargetActor = GetRandomValuableTarget(owner);
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsAttackMoveStateSP(), true);
				return;
			}
			else
			{
				cannotRetaliate = true;
				owner.TargetActor = targetActor;

				foreach (var a in owner.Units)
				{
					if (!BusyAttack(a))
					{
						if (CanAttackTarget(a, targetActor))
						{
							owner.Bot.QueueOrder(new Order("Attack", a, Target.FromActor(owner.TargetActor), false));
							cannotRetaliate = false;
						}
						else
							owner.Bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(owner.World, teamLeader.Location), false));
					}
					else
						cannotRetaliate = false;
				}
			}

			if (ShouldFlee(owner) || cannotRetaliate)
				owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsFleeStateSP(), true);
		}

		public void Deactivate(SquadSP owner) { }
	}

	class GroundUnitsFleeStateSP : GroundStateBaseSP, IState
	{
		public void Activate(SquadSP owner) { }

		public void Tick(SquadSP owner)
		{
			if (!owner.IsValid)
				return;

			Retreat(owner, true, true, true);

			owner.FuzzyStateMachine.ChangeState(owner, new GroundUnitsIdleStateSP(), true);
		}

		public void Deactivate(SquadSP owner) { owner.Units.Clear(); }
	}
}
