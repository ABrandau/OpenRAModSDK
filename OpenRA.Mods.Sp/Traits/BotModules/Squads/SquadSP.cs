#region Copyright & License Information
/*
 * Copyright 2007-2019 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.SP.Traits.BotModules.Squads
{
	public enum SquadTypeSP { Assault, Air, Rush, Protection, Naval }

	public class SquadSP
	{
		public List<Actor> Units = new List<Actor>();
		public SquadTypeSP Type;

		internal IBot Bot;
		internal World World;
		internal SquadManagerBotModuleSP SquadManager;
		internal MersenneTwister Random;

		internal Target Target;
		internal StateMachineSP FuzzyStateMachine;

		public SquadSP(IBot bot, SquadManagerBotModuleSP squadManager, SquadTypeSP type)
			: this(bot, squadManager, type, null) { }

		public SquadSP(IBot bot, SquadManagerBotModuleSP squadManager, SquadTypeSP type, Actor target)
		{
			Bot = bot;
			SquadManager = squadManager;
			World = bot.Player.PlayerActor.World;
			Random = World.LocalRandom;
			Type = type;
			Target = Target.FromActor(target);
			FuzzyStateMachine = new StateMachineSP();

			switch (type)
			{
				case SquadTypeSP.Assault:
				case SquadTypeSP.Rush:
					FuzzyStateMachine.ChangeState(this, new GroundUnitsIdleStateSP(), true);
					break;
				case SquadTypeSP.Air:
					FuzzyStateMachine.ChangeState(this, new AirIdleStateSP(), true);
					break;
				case SquadTypeSP.Protection:
					FuzzyStateMachine.ChangeState(this, new UnitsForProtectionIdleStateSP(), true);
					break;
				case SquadTypeSP.Naval:
					FuzzyStateMachine.ChangeState(this, new NavyUnitsIdleStateSP(), true);
					break;
			}
		}

		public void Update()
		{
			if (IsValid)
				FuzzyStateMachine.Update(this);
		}

		public bool IsValid { get { return Units.Any(); } }

		public Actor TargetActor
		{
			get { return Target.Actor; }
			set { Target = Target.FromActor(value); }
		}

		public bool IsTargetValid
		{
			get { return Target.IsValidFor(Units.FirstOrDefault()) && !Target.Actor.Info.HasTraitInfo<HuskInfo>(); }
		}

		public bool IsTargetVisible
		{
			get { return TargetActor.CanBeViewedByPlayer(Bot.Player); }
		}

		public WPos CenterPosition { get { return Units.Select(u => u.CenterPosition).Average(); } }

		public MiniYaml Serialize()
		{
			var nodes = new MiniYaml("", new List<MiniYamlNode>()
			{
				new MiniYamlNode("Type", FieldSaver.FormatValue(Type)),
				new MiniYamlNode("Units", FieldSaver.FormatValue(Units.Select(a => a.ActorID).ToArray())),
			});

			if (Target.Type == TargetType.Actor)
				nodes.Nodes.Add(new MiniYamlNode("Target", FieldSaver.FormatValue(Target.Actor.ActorID)));

			return nodes;
		}

		public static SquadSP Deserialize(IBot bot, SquadManagerBotModuleSP squadManager, MiniYaml yaml)
		{
			var type = SquadTypeSP.Rush;
			Actor targetActor = null;

			var typeNode = yaml.Nodes.FirstOrDefault(n => n.Key == "Type");
			if (typeNode != null)
				type = FieldLoader.GetValue<SquadTypeSP>("Type", typeNode.Value.Value);

			var targetNode = yaml.Nodes.FirstOrDefault(n => n.Key == "Target");
			if (targetNode != null)
				targetActor = squadManager.World.GetActorById(FieldLoader.GetValue<uint>("ActiveUnits", targetNode.Value.Value));

			var squad = new SquadSP(bot, squadManager, type, targetActor);

			var unitsNode = yaml.Nodes.FirstOrDefault(n => n.Key == "Units");
			if (unitsNode != null)
				squad.Units.AddRange(FieldLoader.GetValue<uint[]>("Units", unitsNode.Value.Value)
					.Select(a => squadManager.World.GetActorById(a)));

			return squad;
		}
	}
}
