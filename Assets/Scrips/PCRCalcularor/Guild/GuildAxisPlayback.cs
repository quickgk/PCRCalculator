using System;
using System.Linq;

namespace PCRCaculator.Guild
{
    // One instance per battle attempt. The game supplies actual input ticks, skill events and accepted UBs.
    public sealed class GuildAxisPlayback
    {
        private readonly GuildAxisPlan plan;
        private readonly Action<bool, int> switches;
        private readonly Action<string> report;
        private int index, mask, once, deferredBit, deferredMask;
        private bool auto, armed, deadlineDue;
        public string Error { get; private set; }
        public int CompletedCues => index;
        private GuildAxisCue Current => Error == null && index < plan.cues.Count ? plan.cues[index] : null;
        private static bool IsEvent(GuildAxisCue c) => c.trigger != GuildAxisTrigger.Immediate && c.trigger != GuildAxisTrigger.RemainingSecond;

        public static string Validate(GuildAxisPlan plan, int playerCount = 5)
        {
            if (plan?.cues == null || plan.cues.Count == 0) return "没有可执行的操作行。";
            if (playerCount < 1 || playerCount > 5) return "编队人数无效。";
            bool ValidMask(int m) => m >= 0 && m < (1 << playerCount);
            if (!ValidMask(plan.openingMask) || !ValidMask(plan.openingOnceMask)) return "开场 SET 超出编队。";
            if (plan.unitIds != null && (plan.unitIds.Length != playerCount || plan.unitIds.Any(id => id <= 0) || plan.unitIds.Distinct().Count() != playerCount)) return "绑定的阵容无效。";
            foreach (var c in plan.cues)
            {
                if (c == null) return "操作轴含有空行。";
                string Bad(string message) => $"第 {c.line} 行：{message}";
                if (!Enum.IsDefined(typeof(GuildAxisCueKind), c.kind) || !Enum.IsDefined(typeof(GuildAxisTrigger), c.trigger)) return Bad("未知操作或触发条件。");
                bool player = c.kind == GuildAxisCueKind.Repeat || c.kind == GuildAxisCueKind.Auto || c.kind == GuildAxisCueKind.Observe;
                if (player ? c.slot < 0 || c.slot >= playerCount : c.slot != -1) return Bad("操作目标不在编队内。");
                if (c.remainingSecond < -1 || c.remainingSecond > 90 || c.afterAuto < -1 || c.afterAuto > 1 || c.afterMask != -1 && !ValidMask(c.afterMask) || !ValidMask(c.afterOnceMask)) return Bad("时间或开关参数无效。");
                if (c.deferredMask != -1 && (!ValidMask(c.deferredMask) || c.afterOnceMask == 0 || (c.afterOnceMask & (c.afterOnceMask-1)) != 0)) return Bad("延后 SET 必须绑定一个明确的角色。");
                if ((c.kind == GuildAxisCueKind.Boss || c.kind == GuildAxisCueKind.Observe) && c.trigger != GuildAxisTrigger.Immediate) return Bad("观察行需要真实 UB。");
                if (c.trigger == GuildAxisTrigger.RemainingSecond && (c.kind != GuildAxisCueKind.Control || c.remainingSecond < 0)) return Bad("倒计时只可触发明确开关操作。");
                if (IsEvent(c) ? c.triggerSlot < -1 || c.triggerSlot >= playerCount : c.triggerSlot != -1) return Bad("条件的目标无效。");
                if (c.deadlineNormalSlot < -1 || c.deadlineNormalSlot >= playerCount || c.deadlineNormalSlot >= 0 && (!IsEvent(c) || !player)) return Bad("普攻命中的截止条件无效。");
                bool named = c.trigger == GuildAxisTrigger.SkillStart || c.trigger == GuildAxisTrigger.SkillEffect;
                if (named ? c.skillId <= 0 : c.skillId != 0) return Bad("技能 ID 与条件不符。");
                if (c.kind == GuildAxisCueKind.Control && c.afterAuto == -1 && c.afterMask == -1 && c.afterOnceMask == 0) return Bad("控制行未指定操作。");
            }
            return null;
        }

        public GuildAxisPlayback(GuildAxisPlan plan, int playerCount, Action<bool,int> applySwitches, Action<string> reportError)
        {
            var error = Validate(plan, playerCount);
            if (error != null) throw new ArgumentException(error, nameof(plan));
            this.plan = plan;
            switches = applySwitches ?? throw new ArgumentNullException(nameof(applySwitches));
            report = reportError ?? throw new ArgumentNullException(nameof(reportError));
            mask = plan.openingMask; once = plan.openingOnceMask; auto = plan.openingAuto;
            Enter(); Publish();
        }

        private void Publish() => switches(auto, mask | once);
        private void Fail(string reason)
        {
            if (Error != null) return;
            Error = $"第 {Current?.line ?? plan.cues[plan.cues.Count-1].line} 行：{reason}；已停止导入轴。";
            auto = false; mask = once = deferredBit = 0;
            Publish(); report(Error);
        }
        private void Apply(GuildAxisCue cue)
        {
            if (cue.afterAuto >= 0) auto = cue.afterAuto == 1;
            if (cue.afterMask >= 0) mask = cue.afterMask;
            once |= cue.afterOnceMask;
            if (cue.deferredMask < 0) return;
            if (deferredBit != 0) { Fail("上一次开一次 SET 尚未等到目标 UB"); return; }
            deferredBit = cue.afterOnceMask; deferredMask = cue.deferredMask;
        }
        private void Enter()
        {
            // Called on opening and synchronously DURING the preceding UB, not when the new actor casts.
            while (Current != null)
            {
                var c = Current;
                armed = c.trigger == GuildAxisTrigger.Immediate; deadlineDue = false;
                if (c.kind == GuildAxisCueKind.Auto) auto = armed;
                if (c.kind != GuildAxisCueKind.Control || !armed) break;
                Apply(c); index++;
            }
        }
        private void Advance()
        {
            var c = Current;
            if (c.kind == GuildAxisCueKind.Auto && (index+1 == plan.cues.Count || plan.cues[index+1].kind != GuildAxisCueKind.Auto)) auto = false;
            Apply(c); index++; Enter(); Publish();
        }
        public void Clock(int second)
        {
            while (Current != null)
            {
                var c = Current;
                bool window = c.remainingSecond >= 0 && (c.kind == GuildAxisCueKind.Observe || c.kind == GuildAxisCueKind.Boss || c.trigger != GuildAxisTrigger.Immediate);
                if (window && second < c.remainingSecond && (!armed || !IsEvent(c))) { Fail($"{c.remainingSecond} 秒未出现标注的事件"); return; }
                if (c.trigger != GuildAxisTrigger.RemainingSecond || c.remainingSecond != second) return;
                Advance();
            }
        }
        public void Skill(int actorSlot, int skillId, bool start, int second)
        {
            Clock(second);
            var c = Current;
            if (c != null && actorSlot >= 0 && c.deadlineNormalSlot == actorSlot && skillId == 1 && !start && (armed || c.remainingSecond < 0 || c.remainingSecond == second)) deadlineDue = true;
            if (c == null || armed || c.triggerSlot != actorSlot || !IsEvent(c) || c.remainingSecond >= 0 && c.remainingSecond != second) return;
            bool normal = c.trigger == GuildAxisTrigger.NormalAttackStart || c.trigger == GuildAxisTrigger.NormalAttackEffect;
            bool wantStart = c.trigger == GuildAxisTrigger.SkillStart || c.trigger == GuildAxisTrigger.NormalAttackStart;
            if (skillId != (normal ? 1 : c.skillId) || start != wantStart) return;
            armed = true;
            if (c.kind == GuildAxisCueKind.Auto) { auto = true; Publish(); }
            else if (c.kind == GuildAxisCueKind.Control) Advance();
        }
        public void Ub(int actorSlot, int second)
        {
            Clock(second);
            if (Error != null) return;
            int bit = actorSlot >= 0 && actorSlot < 5 ? 1 << actorSlot : 0;
            if ((once & bit) != 0)
            {
                once &= ~bit;
                if (deferredBit == bit) { mask = deferredMask; deferredBit = 0; }
                Publish();
            }
            var c = Current;
            if (c == null || c.kind == GuildAxisCueKind.Control || c.slot != actorSlot) return;
            if ((c.kind == GuildAxisCueKind.Observe || c.kind == GuildAxisCueKind.Boss) && c.remainingSecond >= 0 && c.remainingSecond != second) return;
            if (!armed) { Fail("角色在标注条件前已释放 UB"); return; }
            Advance();
        }
        public int PressSlot()
        {
            var c = Current;
            if (c == null || c.kind != GuildAxisCueKind.Repeat || !armed) return -1;
            return c.slot;
        }
        public void AfterInput()
        {
            var c=Current;
            if (c != null && deadlineDue) Fail("目标 UB 未在指定角色普攻命中前或同帧释放");
            else if (c != null && c.kind==GuildAxisCueKind.Repeat && armed && IsEvent(c)) Fail("目押事件已到，但游戏没有接受该次 UB 输入");
        }
        public void Finish()
        {
            if (Current != null) Fail("战斗结束前未对齐此行");
            else if (once != 0 && Error == null) Fail("战斗结束前未等到一次 SET 的目标 UB");
        }
    }
}
