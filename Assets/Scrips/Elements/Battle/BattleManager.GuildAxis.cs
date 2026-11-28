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
        private GuildAxisPlan savedPlan;

        internal void DiscardImportedAxis() => ImportedAxis = null;

        internal void SaveCompletedGuildAxis(MyGameCtrl game)
        {
            var temp = game.tempData;
            if (ImportedAxis == null || ImportedAxis.Error != null || ImportedAxis.CompletedCues != temp.importedAxis.cues.Count) return;
            var settings = GuildManager.StaticsettingData;
            GuildPlayerGroupData group;
            try
            {
                group = settings.SaveRecordedTeam(temp.importedAxisSlot, savedPlan, ubmanager.GetRecordedUbTime(), game.CurrentSeedForSave);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(ex);
                MainManager.Instance.WindowMessage("转换失败，图文轴保留");
                return;
            }
            if (group == null)
            {
                MainManager.Instance.WindowMessage("队伍已变，未转换");
                return;
            }
            // Retry can reuse TempData or rebuild from the launch data. Neither should reapply AUTO/SET.
            var app = MainManager.Instance;
            var launch = app.GuildBattleData;
            temp.SettingData = launch.SettingData = settings;
            temp.recordedAxisTeam = group;
            temp.randomData = group.timeLineData;
            temp.UBExecTimeList = launch.UBExecTimeList = group.UBExecTimeData;
            temp.SemanUBExecTimeList = launch.SemanUBExecTimeList = group.SemanUBExecTimeData;
            temp.SemanUBSequenceData = launch.SemanUBSequenceData = null;
            temp.useSequenceUB = launch.useSequenceUB = false;
            game.IsAutoMode = app.isAutoMode = launch.isAutoMode = false;
            game.IsSetMode = app.isSetMode = launch.isSetMode = false;
            game.IsSemanMode = app.isSemanMode = launch.isSemanMode = false;
            temp.importedAxis = null;
            DiscardImportedAxis();
            GuildManager.Instance?.RefreshAxisTeam(temp.importedAxisSlot);
            app.WindowMessage($"队伍{temp.importedAxisSlot + 1}已转为正常轴");
        }

        private bool BindImportedAxis(MyGameCtrl game)
        {
            var plan = game.tempData.importedAxis;
            if (plan == null) return true;
            var team = game.playerUnitCtrl.ToArray(); // Excludes summons; stable for this attempt.
            string error = GuildAxisPlayback.Validate(plan, team.Length);
            if (error == null && (plan.unitIds == null || !plan.unitIds.SequenceEqual(team.Select(u => u.UnitId)))) error = "阵容不符，请重导图文轴";
            var boss = BossUnit ?? (EnemyList.Count == 1 ? EnemyList[0] : null);
            if (error == null && boss == null && plan.cues.Any(c => c.kind == GuildAxisCueKind.Boss || c.triggerSlot == -1 && c.trigger != GuildAxisTrigger.Immediate && c.trigger != GuildAxisTrigger.RemainingSecond)) error = "图文轴未找到BOSS";
            void Report(string reason)
            {
                Debug.LogWarning(reason);
                int lineEnd = reason.IndexOf(" 行：", System.StringComparison.Ordinal);
                MainManager.Instance?.WindowMessage(lineEnd < 0 ? reason : reason.Substring(0, lineEnd) + " 行失败");
            }
            if (error != null) { Report(error); return false; }
            var axis = new GuildAxisPlayback(plan, team.Length, (auto, mask) => {
                game.IsAutoMode = auto;
#if UNITY_5_3_OR_NEWER
                PCRCaculator.Battle.BattleUIManager.Instance?.AutoToggle?.SetIsOnWithoutNotify(auto);
#endif
                for (int slot = 0; slot < team.Length; slot++) team[slot].pressing = (mask & (1 << slot)) != 0;
            }, Report);
            ImportedAxis = axis;
            // The guild panel can reload the save on scene entry. Bind its copy before the run.
            int teamSlot = game.tempData.importedAxisSlot;
            var groups = GuildManager.StaticsettingData.guildPlayerGroupDatas;
            savedPlan = (uint)teamSlot < (uint)groups.Count ? groups[teamSlot]?.importedAxis : null;
            if (savedPlan != null && !ReferenceEquals(savedPlan, plan) &&
                Newtonsoft.Json.JsonConvert.SerializeObject(savedPlan) != Newtonsoft.Json.JsonConvert.SerializeObject(plan))
                savedPlan = null;
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
