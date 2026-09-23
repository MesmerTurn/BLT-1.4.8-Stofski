using System;
using System.Collections.ObjectModel;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.UI;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using BLTAdoptAHero;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;
using System.ComponentModel;

namespace BLTAdoptAHero.Actions
{
    [LocDisplayName("{=TESTING}DuelCommand"),
     LocDescription("{=TESTING}Challenges another viewer's summoned BLT hero to a 1v1 duel during battle. Usage: !duel @username"),
     UsedImplicitly]
    public class DuelCommand : HeroCommandHandlerBase
    {
        // One entry in the Mark Effect's affected-properties list - a driven property plus a
        // per-property weight, so some stats can scale faster/slower than others from the same
        // stack count without needing a separate percent-per-stack for each.
        public class MarkPropertyModifier
        {
            [LocDisplayName("{=TESTING}Property"),
             LocDescription("{=TESTING}The driven property this stack effect modifies"),
             PropertyOrder(1), UsedImplicitly]
            public DrivenProperty Name { get; set; } = DrivenProperty.ArmorTorso;

            [LocDisplayName("{=TESTING}Weight"),
             LocDescription("{=TESTING}Multiplier applied to \"Percent Per Stack\" for this specific property. 1 = normal scaling, 2 = double, 0.5 = half"),
             PropertyOrder(2), UsedImplicitly]
            public float Weight { get; set; } = 1f;

            public override string ToString() => $"{Name} x{Weight}";
        }

        [CategoryOrder("General", 0),
         CategoryOrder("Gold", 1),
         CategoryOrder("Mark Effect", 2),
         CategoryOrder("Visuals", 3)]
        public class Settings : IDocumentable
        {
            [LocDisplayName("{=TESTING}Gold Rewards"),
             LocCategory("Gold", "{=TESTING}Gold"),
             LocDescription("{=TESTING}Gold awarded for winning either side of a duel"),
             ExpandableObject, Expand, PropertyOrder(1), UsedImplicitly]
            public GoldSettings Gold { get; set; } = new();

            [LocDisplayName("{=TESTING}Duel Mark"),
             LocCategory("Mark Effect", "{=TESTING}Mark Effect"),
             LocDescription("{=TESTING}A stacking effect applied to a hero each time they're challenged to a duel"),
             ExpandableObject, Expand, PropertyOrder(1), UsedImplicitly]
            public MarkEffectSettings Mark { get; set; } = new();

            [LocDisplayName("{=TESTING}Attacker Particle Effect"),
             LocCategory("Visuals", "{=TESTING}Visuals"),
             LocDescription("{=TESTING}One-shot particle effect played on the challenger when a duel starts. Leave empty to disable"),
             ItemsSource(typeof(OneShotParticleEffectItemSource)),
             PropertyOrder(1), UsedImplicitly]
            public string AttackerParticleEffect { get; set; } = "";

            public void GenerateDocumentation(IDocumentationGenerator generator)
            {
                generator.Value("<strong>Usage:</strong> @username");
                generator.Value($"Gold: +{Gold.GoldOnDuelKill} to the challenger on a duel kill, +{Gold.GoldOnDefendKill} to a hero who kills their challenger");
                if (Mark.Enabled)
                {
                    string capText = Mark.MaxStacks > 0 ? $"up to {Mark.MaxStacks} stacks" : "unlimited stacks";
                    generator.Value($"Duel Mark: {Mark.PercentPerStack:+0;-0}% per stack ({capText})");
                }
            }
        }

        [CategoryOrder("General", 0)]
        public class GoldSettings
        {
            [LocDisplayName("{=TESTING}Gold On Duel Kill"),
             LocCategory("General", "{=TESTING}General"),
             LocDescription("{=TESTING}Gold awarded to the challenger when they kill their duel target"),
             PropertyOrder(1), UsedImplicitly]
            public int GoldOnDuelKill { get; set; } = 20000;

            [LocDisplayName("{=TESTING}Gold On Defend Kill"),
             LocCategory("General", "{=TESTING}General"),
             LocDescription("{=TESTING}Gold awarded to a hero when they kill someone who had challenged THEM to a duel"),
             PropertyOrder(2), UsedImplicitly]
            public int GoldOnDefendKill { get; set; } = 20000;
        }

        [CategoryOrder("General", 0),
         CategoryOrder("Properties", 1),
         CategoryOrder("Visuals", 2)]
        public class MarkEffectSettings
        {
            [LocDisplayName("{=TESTING}Enabled"),
             LocCategory("General", "{=TESTING}General"),
             LocDescription("{=TESTING}Whether being challenged to a duel applies a stacking effect to the target"),
             PropertyOrder(1), UsedImplicitly]
            public bool Enabled { get; set; } = true;

            [LocDisplayName("{=TESTING}Max Stacks"),
             LocCategory("General", "{=TESTING}General"),
             LocDescription("{=TESTING}Maximum number of stacks a hero can carry at once. 0 = unlimited"),
             PropertyOrder(2), UsedImplicitly]
            public int MaxStacks { get; set; } = 10;

            [LocDisplayName("{=TESTING}Percent Per Stack"),
             LocCategory("General", "{=TESTING}General"),
             LocDescription("{=TESTING}Percentage change per stack, applied to every property below. Positive = buff, negative = debuff"),
             PropertyOrder(3), UsedImplicitly]
            public float PercentPerStack { get; set; } = 5f;

            [LocDisplayName("{=TESTING}Stack Duration (seconds)"),
             LocCategory("General", "{=TESTING}General"),
             LocDescription("{=TESTING}How long a single duel's contribution to the stack lasts before it can be refreshed/replaced by a new duel from the same challenger, in seconds. 0 = lasts for the rest of the battle"),
             PropertyOrder(4), UsedImplicitly]
            public float StackDurationSeconds { get; set; } = 120f;

            [LocDisplayName("{=TESTING}Affected Properties"),
             LocCategory("Properties", "{=TESTING}Properties"),
             LocDescription("{=TESTING}Which driven properties this effect modifies, and how strongly each scales"),
             Editor(typeof(DefaultCollectionEditor), typeof(DefaultCollectionEditor)),
             PropertyOrder(1), UsedImplicitly]
            public ObservableCollection<MarkPropertyModifier> AffectedProperties { get; set; } = new()
            {
                new MarkPropertyModifier { Name = DrivenProperty.SwingSpeedMultiplier, Weight = 1f },
                new MarkPropertyModifier { Name = DrivenProperty.DamageMultiplierBonus, Weight = 1f },
                new MarkPropertyModifier { Name = DrivenProperty.MaxSpeedMultiplier, Weight = -1f },
            };

            [LocDisplayName("{=TESTING}Show Contour"),
             LocCategory("Visuals", "{=TESTING}Visuals"),
             LocDescription("{=TESTING}Highlights marked heroes with a colored outline - visual only"),
             PropertyOrder(1), UsedImplicitly]
            public bool ShowContour { get; set; } = false;

            [LocDisplayName("{=TESTING}Contour Color (hex AARRGGBB)"),
             LocCategory("Visuals", "{=TESTING}Visuals"),
             LocDescription("{=TESTING}Outline color shown on marked heroes, as an 8-digit hex code"),
             PropertyOrder(2), UsedImplicitly]
            public string ContourColor { get; set; } = "FFFFD700";

            [LocDisplayName("{=TESTING}Target Particle Effect"),
             LocCategory("Visuals", "{=TESTING}Visuals"),
             LocDescription("{=TESTING}One-shot particle effect played on the target each time a new stack is applied. Leave empty to disable"),
             ItemsSource(typeof(OneShotParticleEffectItemSource)),
             PropertyOrder(3), UsedImplicitly]
            public string TargetParticleEffect { get; set; } = "";
        }

        public override Type HandlerConfigType => typeof(Settings);

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            if (config is not Settings settings) return;
            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }

            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            {
                onFailure("Duel can only be challenged during an active battle.");
                return;
            }

            // Block during deployment: agents are still lined up on their own side, so ordering
            // one to run alone across no-man's-land into a full enemy formation gets it swarmed
            // and killed instantly on arrival.
            if (!Mission.Current.IsDeploymentFinished)
            {
                onFailure("Duel can't be started during deployment - wait for the battle to actually begin.");
                return;
            }

            var behavior = Mission.Current.GetMissionBehavior<BLTDuelBehavior>();
            if (behavior == null)
            {
                onFailure("Duel system is not active.");
                return;
            }

            string targetName = (context.Args ?? "").Trim().TrimStart('@');
            if (string.IsNullOrEmpty(targetName))
            {
                onFailure("Usage: !duel @username");
                return;
            }

            var targetHero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(targetName);
            if (targetHero == null)
            {
                onFailure($"BLT hero '{targetName}' not found.");
                return;
            }
            if (targetHero == adoptedHero)
            {
                onFailure("You can't challenge yourself.");
                return;
            }

            var challengerAgent = BLTSummonBehavior.Current?.GetHeroSummonState(adoptedHero)?.CurrentAgent;
            var targetAgent = BLTSummonBehavior.Current?.GetHeroSummonState(targetHero)?.CurrentAgent;

            if (challengerAgent == null || !challengerAgent.IsActive())
            {
                onFailure("Your hero is not summoned in this battle.");
                return;
            }
            if (targetAgent == null || !targetAgent.IsActive())
            {
                onFailure($"{targetHero.FirstName} is not present in this battle.");
                return;
            }

            if (challengerAgent.Team == targetAgent.Team)
            {
                onFailure($"{adoptedHero.FirstName} and {targetHero.FirstName} are on the same side — duel impossible!");
                return;
            }

            // The challenger can't attack two targets at once. The target CAN be attacked by
            // multiple heroes at once (gang up on one enemy).
            if (behavior.HasActiveDuel(adoptedHero))
            {
                onFailure("You're already in a duel — finish it first.");
                return;
            }

            behavior.StartDuel(adoptedHero, targetHero, settings);

            var msg = $"⚔ DUEL! {adoptedHero.FirstName} challenged {targetHero.FirstName}! Let the fight begin!";
            onSuccess(msg);
            Log.ShowInformation(msg, adoptedHero.CharacterObject);
        }
    }
}