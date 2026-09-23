using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Util;
using BLTAdoptAHero.Actions;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    // Tracks active 1v1 duel challenges between summoned BLT heroes during a mission, plus a
    // stacking "duel mark" effect (buff or debuff, configurable) applied to heroes each time
    // they're challenged, and gold rewards for winning either side of a duel.
    internal class BLTDuelBehavior : MissionBehavior
    {
        public static BLTDuelBehavior Current { get; private set; }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private class DuelInfo
        {
            public Hero Target;
            public DuelCommand.Settings Settings;
        }

        // attacker -> duel info (target + the settings this specific duel was started with)
        private readonly Dictionary<Hero, DuelInfo> _activeDuels = new();

        // Mark contributions: target -> (attacker -> expiry time, -1 = no expiry / rest of battle).
        // One contribution per (target, attacker) pair - re-challenging the same target while
        // your previous contribution hasn't expired just refreshes it rather than adding a stack.
        private readonly Dictionary<Hero, Dictionary<Hero, float>> _markContributions = new();
        // The AgentModifierConfig currently applied to each marked target, so it can be removed
        // cleanly before a replacement is applied (or on expiry/death).
        private readonly Dictionary<Hero, AgentModifierConfig> _markConfigs = new();

        private float _nextRetargetTime;
        private const float RetargetInterval = 1.5f;
        private const float ExpirySweepInterval = 1f;
        private float _nextExpirySweep;

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            Current = this;
        }

        public override void OnRemoveBehavior()
        {
            base.OnRemoveBehavior();
            if (Current == this) Current = null;
        }

        public bool HasActiveDuel(Hero hero) => hero != null && _activeDuels.ContainsKey(hero);

        private static Agent GetHeroAgent(Hero hero)
            => BLTSummonBehavior.Current?.GetHeroSummonState(hero)?.CurrentAgent;

        public void StartDuel(Hero attacker, Hero target, DuelCommand.Settings settings)
        {
            if (attacker == null || target == null || settings == null) return;
            _activeDuels[attacker] = new DuelInfo { Target = target, Settings = settings };

            var attackerAgent = GetHeroAgent(attacker);
            var targetAgent = GetHeroAgent(target);
            DuelMoveAndFight(attackerAgent, targetAgent);

            ApplyMarkContribution(target, attacker, settings);
            PlayBurst(attackerAgent, settings.AttackerParticleEffect);
        }

        public override void OnMissionTick(float dt)
        {
            float now = Mission.Current?.CurrentTime ?? 0f;

            if (_activeDuels.Count > 0 && now >= _nextRetargetTime)
            {
                _nextRetargetTime = now + RetargetInterval;
                RefreshDuels();
            }

            if (_markContributions.Count > 0 && now >= _nextExpirySweep)
            {
                _nextExpirySweep = now + ExpirySweepInterval;
                SweepExpiredContributions(now);
            }
        }

        private void RefreshDuels()
        {
            // Ongoing movement/engagement for still-valid duels. Kills are handled (with gold +
            // cleanup) in OnAgentRemoved; this just drops stale entries left behind by edge cases
            // (agent gone without a Killed state, e.g. removed from the mission entirely).
            var toRemove = new List<Hero>();

            foreach (var (attacker, info) in _activeDuels.ToList())
            {
                var attackerAgent = GetHeroAgent(attacker);
                var targetAgent = GetHeroAgent(info.Target);

                if (attackerAgent == null || !attackerAgent.IsActive() ||
                    targetAgent == null || !targetAgent.IsActive())
                {
                    toRemove.Add(attacker);
                    continue;
                }

                DuelMoveAndFight(attackerAgent, targetAgent);
            }

            foreach (var h in toRemove)
                _activeDuels.Remove(h);
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            try
            {
                if (agentState != AgentState.Killed || affectorAgent == null || affectedAgent == null) return;

                var victim = affectedAgent.GetAdoptedHero();
                var killer = affectorAgent.GetAdoptedHero();
                if (victim == null || killer == null) return;

                // Killer was the CHALLENGER, victim was their duel target.
                if (_activeDuels.TryGetValue(killer, out var duelInfo) && duelInfo.Target == victim)
                {
                    int gold = duelInfo.Settings.Gold.GoldOnDuelKill;
                    if (gold > 0)
                        BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(killer, gold, true);
                    Log.ShowInformation(
                        $"🏆 {killer.FirstName} defeated their duel target {victim.FirstName}! (+{gold} gold)",
                        killer.CharacterObject);
                    _activeDuels.Remove(killer);
                    return;
                }

                // Killer was the TARGET, victim was the one who had challenged them.
                if (_activeDuels.TryGetValue(victim, out var reverseInfo) && reverseInfo.Target == killer)
                {
                    int gold = reverseInfo.Settings.Gold.GoldOnDefendKill;
                    if (gold > 0)
                        BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(killer, gold, true);
                    Log.ShowInformation(
                        $"🛡 {killer.FirstName} fended off {victim.FirstName}'s duel challenge! (+{gold} gold)",
                        killer.CharacterObject);
                    _activeDuels.Remove(victim);
                }
            }
            catch (Exception ex) { Log.Exception("BLTDuelBehavior.OnAgentRemoved", ex); }
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            var hero = affectedAgent?.GetAdoptedHero();
            if (hero == null) return;
            // Agent is gone - drop tracking only, don't touch the (destroyed) agent.
            ClearMark(hero, touchAgent: false);
        }

        // Attacker runs to the duel target, but fights anything that engages it along the way
        // (via FollowCombat's engage radius), and prioritizes the duel target once in range.
        private static void DuelMoveAndFight(Agent attacker, Agent target)
        {
            if (attacker == null || !attacker.IsActive() || target == null || !target.IsActive()) return;
            try
            {
                float dist = (attacker.Position - target.Position).Length;
                if (dist <= FollowCombat.EngageRange || FollowCombat.HasEnemyNear(attacker, FollowCombat.EngageRange))
                {
                    attacker.SetAutomaticTargetSelection(true);
                    attacker.DisableScriptedMovement();
                    if (dist <= FollowCombat.EngageRange)
                        attacker.SetTargetAgent(target);
                    return;
                }

                var pos = target.GetWorldPosition();
                attacker.SetScriptedPosition(ref pos, false, Agent.AIScriptedFrameFlags.None);
                attacker.SetTargetAgent(target);
            }
            catch { }
        }

        // --- Duel Mark (stacking effect on the target side) ---

        private void ApplyMarkContribution(Hero target, Hero attacker, DuelCommand.Settings settings)
        {
            var mark = settings.Mark;
            if (!mark.Enabled) return;

            var agent = GetHeroAgent(target);
            if (agent == null || !agent.IsActive()) return;

            if (!_markContributions.TryGetValue(target, out var contributions))
            {
                contributions = new Dictionary<Hero, float>();
                _markContributions[target] = contributions;
            }

            float now = Mission.Current?.CurrentTime ?? 0f;
            float expiry = mark.StackDurationSeconds > 0f ? now + mark.StackDurationSeconds : -1f;

            bool hadValidContribution = contributions.TryGetValue(attacker, out var existingExpiry)
                                         && (existingExpiry < 0f || existingExpiry > now);

            // Same attacker, still-active contribution: refresh the timer, don't add a new stack.
            contributions[attacker] = expiry;

            if (!hadValidContribution)
            {
                PlayBurst(agent, mark.TargetParticleEffect);
                int newCount = CountValidContributions(contributions, now);
                Log.ShowInformation(
                    $"{target.FirstName} bears {newCount} duel mark{(newCount == 1 ? "" : "s")}!",
                    target.CharacterObject);
            }

            RecomputeMark(target, agent, mark);
        }

        private static int CountValidContributions(Dictionary<Hero, float> contributions, float now)
            => contributions.Count(kv => kv.Value < 0f || kv.Value > now);

        private void SweepExpiredContributions(float now)
        {
            foreach (var target in _markContributions.Keys.ToList())
            {
                var contributions = _markContributions[target];
                var expired = contributions.Where(kv => kv.Value >= 0f && kv.Value <= now)
                    .Select(kv => kv.Key).ToList();
                if (expired.Count == 0) continue;

                foreach (var attacker in expired)
                    contributions.Remove(attacker);

                if (contributions.Count == 0)
                {
                    _markContributions.Remove(target);
                    ClearMark(target, touchAgent: true);
                    continue;
                }

                // Stacks changed - recompute with whatever settings applied the most recent
                // contribution still active for this target. We don't track per-contribution
                // settings, so we simply reuse whatever config is already attached; if none of the
                // remaining duels are configured differently this is exactly correct, and even in
                // a mixed-config edge case the effect just rescales to the current count.
                var agent = GetHeroAgent(target);
                if (agent != null && agent.IsActive())
                {
                    // Fall back to a matching active duel's settings if one exists, else skip
                    // (config unreachable - leave existing modifier as-is until next contribution).
                    var settingsSource = _activeDuels.Values.FirstOrDefault(d => d.Target == target)?.Settings;
                    if (settingsSource != null)
                        RecomputeMark(target, agent, settingsSource.Mark);
                }
            }
        }

        private void RecomputeMark(Hero target, Agent agent, DuelCommand.MarkEffectSettings mark)
        {
            if (!_markContributions.TryGetValue(target, out var contributions)) return;

            float now = Mission.Current?.CurrentTime ?? 0f;
            int validCount = CountValidContributions(contributions, now);
            if (validCount <= 0)
            {
                ClearMark(target, touchAgent: true);
                return;
            }

            int effectiveStacks = mark.MaxStacks > 0 ? Math.Min(validCount, mark.MaxStacks) : validCount;

            // Remove the previous modifier before applying the recomputed one.
            if (_markConfigs.TryGetValue(target, out var oldConfig))
            {
                try { BLTAgentModifierBehavior.Current?.Remove(agent, oldConfig); } catch { }
                _markConfigs.Remove(target);
            }

            var newConfig = new AgentModifierConfig();
            foreach (var prop in mark.AffectedProperties ?? Enumerable.Empty<DuelCommand.MarkPropertyModifier>())
            {
                float percent = mark.PercentPerStack * effectiveStacks * prop.Weight;
                float modifierPercent = Math.Max(0f, 100f + percent);
                newConfig.Properties.Add(new PropertyModifierDef { Name = prop.Name, ModifierPercent = modifierPercent });
            }

            if (newConfig.Properties.Count > 0)
            {
                try { BLTAgentModifierBehavior.Current?.Add(agent, newConfig); } catch { }
                _markConfigs[target] = newConfig;
            }

            if (mark.ShowContour)
            {
                try
                {
                    uint color = Convert.ToUInt32(mark.ContourColor, 16);
                    agent.AgentVisuals?.SetContourColor(color, true);
                }
                catch { }
            }
        }

        private void ClearMark(Hero hero, bool touchAgent)
        {
            _markContributions.Remove(hero);

            if (_markConfigs.TryGetValue(hero, out var config))
            {
                if (touchAgent)
                {
                    var agent = GetHeroAgent(hero);
                    if (agent != null && agent.IsActive())
                    {
                        try { BLTAgentModifierBehavior.Current?.Remove(agent, config); } catch { }
                        try { agent.AgentVisuals?.SetContourColor(null, false); } catch { }
                    }
                }
                _markConfigs.Remove(hero);
            }
        }

        private static void PlayBurst(Agent agent, string particleEffectName)
        {
            if (string.IsNullOrEmpty(particleEffectName)) return;
            if (agent?.AgentVisuals == null || Mission.Current?.Scene == null) return;
            try
            {
                Mission.Current.Scene.CreateBurstParticle(
                    ParticleSystemManager.GetRuntimeIdByName(particleEffectName),
                    agent.AgentVisuals.GetGlobalFrame());
            }
            catch { }
        }
    }
}