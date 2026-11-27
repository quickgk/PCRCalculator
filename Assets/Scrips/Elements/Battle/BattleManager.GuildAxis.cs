using System.Linq;
using PCRCaculator;
using PCRCaculator.Guild;
using UnityEngine;

namespace Elements.Battle
{
    public partial class BattleManager
    {
        internal GuildAxisPlayback ImportedAxis { get; private set; }
        private int AxisSecond => Mathf.CeilToInt(BattleLeftTime);

        private bool BindImportedAxis(MyGameCtrl game)
        {
            var plan = game.tempData.importedAxis;
            if (plan == null) return true;
            var team = game.playerUnitCtrl.ToArray(); // Excludes summons; stable for this attempt.
            string error = GuildAxisPlayback.Validate(plan, team.Length);
            if (error == null && (plan.unitIds == null || !plan.unitIds.SequenceEqual(team.Select(u => u.UnitId)))) error = "图文轴的角色或站位已变化，请重新导入。";
            var boss = BossUnit ?? (EnemyList.Count == 1 ? EnemyList[0] : null);
            if (error == null && boss == null && plan.cues.Any(c => c.kind == GuildAxisCueKind.Boss || c.triggerSlot == -1 && c.trigger != GuildAxisTrigger.Immediate && c.trigger != GuildAxisTrigger.RemainingSecond)) error = "图文轴不能唯一确定主 BOSS。";
            void Report(string reason) { UnityEngine.Debug.LogWarning(reason); MainManager.Instance?.WindowMessage(reason); }
            if (error != null) { Report(error); return false; }
            var axis = new GuildAxisPlayback(plan, team.Length, (auto, mask) => {
                game.IsAutoMode = auto;
#if UNITY_5_3_OR_NEWER
                PCRCaculator.Battle.BattleUIManager.Instance?.AutoToggle?.SetIsOnWithoutNotify(auto);
#endif
                for (int slot = 0; slot < team.Length; slot++) team[slot].pressing = (mask & (1 << slot)) != 0;
            }, Report);
            ImportedAxis = axis;
            void Watch(UnitCtrl unit, int slot)
            {
                unit.SemanOnChangeState += (_, state) => {
                    if (state == UnitCtrl.ActionState.SKILL_1 && ReferenceEquals(axis, ImportedAxis)) axis.Ub(slot, AxisSecond);
                };
                unit.SemanOnStartAction += (_, skill) => { if (ReferenceEquals(axis, ImportedAxis)) axis.Skill(slot, skill, true, AxisSecond); };
                unit.AxisOnExecAction += (_, skill) => { if (ReferenceEquals(axis, ImportedAxis)) axis.Skill(slot, skill, false, AxisSecond); };
            }
            for (int slot = 0; slot < team.Length; slot++) Watch(team[slot], slot);
            if (boss != null) Watch(boss, -1);
            return true;
        }
    }
}
