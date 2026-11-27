using System;
using System.Collections.Generic;

namespace PCRCaculator.Guild
{
    public enum GuildAxisCueKind { Repeat, Auto, Boss, Control, Observe }
    public enum GuildAxisTrigger { Immediate, NormalAttackEffect, SkillStart, SkillEffect, RemainingSecond, NormalAttackStart }

    [Serializable]
    public sealed class GuildAxisPlan
    {
        public int[] unitIds;
        public int openingMask, openingOnceMask;
        public bool openingAuto;
        public List<GuildAxisCue> cues = new List<GuildAxisCue>();
    }

    [Serializable]
    public sealed class GuildAxisCue
    {
        public int line;
        public GuildAxisCueKind kind;
        public int slot = -1, remainingSecond = -1;
        public GuildAxisTrigger trigger;
        public int triggerSlot = -1, skillId, deadlineNormalSlot = -1;
        public int afterMask = -1, afterAuto = -1, afterOnceMask, deferredMask = -1;
    }
}
