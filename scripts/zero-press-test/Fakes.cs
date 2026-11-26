// Deliberately inert DTOs: do not instantiate game objects, read assets or start a battle.
using Elements.Battle;
namespace Elements
{
    public class UnitCtrl
    {
        public enum ActionState { IDLE, SKILL_1 }
        public bool IsOther, IsBoss, IsSummonOrPhantom, IsDivision, IsBonusEnemy, pressing;
        public int UnitId, enemyId, CurrentSkillId, UnionBurstSkillId;
        public ActionState CurrentState;
        public UnitActionController unitActionController = new();
    }
    public class Skill { public float BlackOutTime; }
    public class UnitActionController { public Dictionary<int, Skill> skillDictionary = new(); }
    public class Player { public int unitId; }
    public class Enemy { public int enemy_id, unit_id; }
    public class Temp { public List<Enemy> guildEnemy = new(); }
    public static class BattleHeaderController { public static int CurrentFrameCount; }
    public partial class MyGameCtrl
    {
        public bool IsAutoMode, ForceAutoMode;
        public Temp tempData = new();
        public void StartSynthetic(BattleSimulationRequest req) => BeginZeroPress(req);
    }
}
namespace Elements.Battle
{
    public class BattleSimulationRequest
    {
        public bool IsGuildBattle = true, IsSemanMode, UseSequenceUB;
        public List<Player> PlayerList = new();
        public List<List<float>> UBExecTimeList;
        public List<List<int>> SemanUBExecTimeList;
        public List<int> SemanUBSequenceData;
        public ZeroPressPolicyData ZeroPressPolicy;
    }
    public class BattleManager
    {
        public const float DeltaTime_60fps = 1f / 60;
        public static BattleManager Instance = new();
        public float Remaining = 1f;
        public float ZeroPressBlackoutRemaining(UnitCtrl owner) => Remaining;
        public bool IsFramePause;
        public UnitCtrl BossUnit;
        public List<UnitCtrl> BlackOutUnitList = new();
        public object scriptMgr;
    }
}
namespace PCRCaculator.Guild
{
    public class GuildManager
    {
        public static GuildManager Instance = new();
        public bool IsGuildBossId(int id) => id / 100000000 == 4;
    }
}
