using System;
using System.Linq;
using Elements.Battle;
using PCRCaculator.Guild;

namespace Elements
{
    public partial class MyGameCtrl
    {
        internal ZeroPressRun<UnitCtrl> ZeroPress { get; private set; }

        // OnCutInEnd invokes this synchronously after successful native ConsumeEnergy
        // and SetState(SKILL_1); no other actor can arbitrate a UB in between.
        internal void OnZeroCommittedCutIn(UnitCtrl actor, BattleManager battle)
        {
            if (ZeroPress == null || ForceAutoMode || actor.IsSummonOrPhantom || actor.IsDivision || actor.IsBonusEnemy ||
                actor.CurrentState != UnitCtrl.ActionState.SKILL_1 || actor.UnionBurstSkillId <= 0 || actor.CurrentSkillId != actor.UnionBurstSkillId) return;
            var inBlackout = battle.BlackOutUnitList.Contains(actor);
            if (!actor.IsOther)
            {
                if (battle.IsFramePause || inBlackout)
                    ZeroPress.OnPlayerCutIn(actor, BattleHeaderController.CurrentFrameCount);
                return;
            }
            if (!actor.IsBoss || !ReferenceEquals(actor, battle.BossUnit) || !inBlackout ||
                !actor.unitActionController.skillDictionary.TryGetValue(actor.UnionBurstSkillId, out var skill)) return;
            var primary = tempData.guildEnemy[0];
            if (GuildManager.Instance.IsGuildBossId(primary.enemy_id) && actor.enemyId == primary.enemy_id && actor.UnitId == primary.unit_id)
                ZeroPress.OnBossBlackout(Math.Min(skill.BlackOutTime, battle.ZeroPressBlackoutRemaining(actor)), BattleHeaderController.CurrentFrameCount);
        }

        private void BeginZeroPress(BattleSimulationRequest req)
        {
            ZeroPress = null; // MyGameCtrl is thread-local, but it is reused for the next seed.
            if (req.ZeroPressPolicy == null) return;
            if (!req.IsGuildBattle || req.IsSemanMode || req.UseSequenceUB ||
                req.UBExecTimeList?.Any(x => x?.Count > 0) == true ||
                req.SemanUBExecTimeList?.Any(x => x?.Count > 0) == true || req.SemanUBSequenceData?.Count > 0)
                throw new ArgumentException("ZeroPressPolicy requires guild battle without UB schedules, sequence or semantic mode.");
            ZeroPress = new ZeroPressRun<UnitCtrl>(req.ZeroPressPolicy, req.PlayerList.Select(p => p.unitId),
                value => IsAutoMode = value, (unit, value) => unit.pressing = value);
            BattleManager.Instance.scriptMgr = null;
        }
    }
}
