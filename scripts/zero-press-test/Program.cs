using System.Text.Json;
using Elements;
using Elements.Battle;

var tests = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); ++tests; }
void Throws<E>(Action a, string message) where E : Exception {
    try { a(); } catch (E) { tests++; return; } throw new Exception(message);
}
var first = new UnitCtrl { UnitId = 101 };
var second = new UnitCtrl { UnitId = 202 };
var boss = new UnitCtrl { UnitId = 303, enemyId = 400000001, IsOther = true, IsBoss = true,
    UnionBurstSkillId = 9, CurrentSkillId = 9, CurrentState = UnitCtrl.ActionState.SKILL_1 };
boss.unitActionController.skillDictionary[9] = new() { BlackOutTime = 2 };
var skill = boss.unitActionController.skillDictionary[9];
var battle = BattleManager.Instance;
battle.BossUnit = boss;
battle.BlackOutUnitList.Add(boss);
var game = new MyGameCtrl { tempData = new Temp { guildEnemy = new() {new Enemy { enemy_id=boss.enemyId, unit_id=boss.UnitId }} } };
var policy = JsonSerializer.Deserialize<ZeroPressPolicyData>("""
{"OpeningAuto":false,"OpeningMask":2,"Rules":[{"UnitId":101,"Occurrence":2,"Auto":true,"Mask":0},{"UnitId":0,"Occurrence":1,"Auto":false,"Mask":1}]}
""");
var req = new BattleSimulationRequest { ZeroPressPolicy = policy, PlayerList = new() {new() {unitId=101}, new() {unitId=202}} };
var roster = new Dictionary<int, UnitCtrl> {{202,second},{101,first}};
game.StartSynthetic(req); var run = game.ZeroPress;
Throws<InvalidOperationException>(()=>run.TraceSnapshot(), "unapplied opening returned success");
run.Open(roster);
Check(run.Opening == new ZeroPressState(false,2) && !game.IsAutoMode && second.pressing && !first.pressing, "mask must follow request, not internal sort");
Check(run.TraceSnapshot().Count==0,"opening must not fabricate an occurrence");
Throws<InvalidOperationException>(()=>run.Open(roster),"second opening");
BattleHeaderController.CurrentFrameCount=500;
void P(UnitCtrl actor) => game.OnZeroCommittedCutIn(actor,battle);
void B() => P(boss);
first.CurrentState=second.CurrentState=UnitCtrl.ActionState.SKILL_1;
first.UnionBurstSkillId=first.CurrentSkillId=second.UnionBurstSkillId=second.CurrentSkillId=8;
P(first); // no paused frame and no actor blackout
battle.IsFramePause=true;
first.CurrentState=UnitCtrl.ActionState.IDLE; P(first); first.CurrentState=UnitCtrl.ActionState.SKILL_1;
first.CurrentSkillId++; P(first); first.CurrentSkillId--;
P(new UnitCtrl { UnitId=101, CurrentState=UnitCtrl.ActionState.SKILL_1, CurrentSkillId=8,UnionBurstSkillId=8 });
first.IsSummonOrPhantom=true; P(first); first.IsSummonOrPhantom=false;
first.IsDivision=true; P(first); first.IsDivision=false;
game.ForceAutoMode=true; P(first); B(); game.ForceAutoMode=false;
Check(run.TraceSnapshot().Count==0,"non-UB state, failed/unstopped player UB or clone counted");
P(first);
battle.BossUnit=new UnitCtrl(); B(); battle.BossUnit=boss; // other boss, same side
boss.enemyId++; B(); boss.enemyId--;
boss.UnitId++; B(); boss.UnitId--;
boss.IsOther=false; B(); boss.IsOther=true;
boss.IsBoss=false; B(); boss.IsBoss=true;
boss.IsDivision=true; B(); boss.IsDivision=false;
boss.IsSummonOrPhantom=true; B(); boss.IsSummonOrPhantom=false;
boss.IsBonusEnemy=true; B(); boss.IsBonusEnemy=false;
boss.CurrentSkillId++; B(); boss.CurrentSkillId--;
boss.CurrentState=UnitCtrl.ActionState.IDLE; B(); boss.CurrentState=UnitCtrl.ActionState.SKILL_1;
game.tempData.guildEnemy[0].enemy_id=boss.enemyId=700000001; B(); game.tempData.guildEnemy[0].enemy_id=boss.enemyId=400000001;
skill.BlackOutTime=0; B(); skill.BlackOutTime=1f/60; B(); skill.BlackOutTime=0.999f; B();
skill.BlackOutTime=1f; battle.Remaining=0.999f; B(); battle.Remaining=1f;
battle.BlackOutUnitList.Clear(); B(); battle.BlackOutUnitList.Add(boss);
Check(run.TraceSnapshot().Count==1,"ineligible actor/skill/instant blackout counted");
B();
Check(!game.IsAutoMode && first.pressing && !second.pressing,"qualified boss window");
P(second); // same frame, independent counts
var frozen = run.TraceSnapshot();
BattleHeaderController.CurrentFrameCount=654;
P(first); B();
Check(game.IsAutoMode && !first.pressing && !second.pressing,"second player UB changes live controls");
ZeroPressEvent[] expected = [new(101,1,500,false,2,false),new(0,1,500,false,1,true),new(202,1,500,false,1,false),new(101,2,654,true,0,true),new(0,2,654,true,0,false)];
Check(expected.SequenceEqual(run.TraceSnapshot()),"trace ordering/counts/persistent effective state");
Check(frozen.Count==3 && run.Opening.Mask==2,"snapshots must not drift");
// A new seed sees its own event stream, even on a reused worker; no previous time is consulted.
game.StartSynthetic(req); game.ZeroPress.Open(roster); BattleHeaderController.CurrentFrameCount=321; P(second);
Check(game.ZeroPress.TraceSnapshot().Single()==new ZeroPressEvent(202,1,321,false,2,false),"new seed/worker reuse");
var copy=policy.Copy(); copy.Rules[0].Mask=3; Check(policy.Rules[0].Mask==0,"deep copy");
req.UBExecTimeList = new() { new() {1} }; Throws<ArgumentException>(()=>game.StartSynthetic(req),"mixed frame schedule"); req.UBExecTimeList=null;
req.UseSequenceUB=true; Throws<ArgumentException>(()=>game.StartSynthetic(req),"mixed sequence"); req.UseSequenceUB=false;
req.IsSemanMode=true; Throws<ArgumentException>(()=>game.StartSynthetic(req),"mixed semantic mode"); req.IsSemanMode=false;
req.ZeroPressPolicy=null; game.StartSynthetic(req); Check(game.ZeroPress==null,"ordinary request inherited previous policy");
req.ZeroPressPolicy=policy.Copy(); req.ZeroPressPolicy.OpeningMask=32; Throws<ArgumentException>(()=>game.StartSynthetic(req),"bad opening");
foreach (var bad in new ZeroPressRule[] {new() {UnitId=999,Occurrence=1},new() {UnitId=-1,Occurrence=1},new() {UnitId=0,Occurrence=0},new() {UnitId=0,Occurrence=1,Mask=-1},new() {UnitId=101,Occurrence=2}}) {
    req.ZeroPressPolicy=policy.Copy(); req.ZeroPressPolicy.Rules.Add(bad); Throws<ArgumentException>(()=>game.StartSynthetic(req),"invalid rule");
}
Throws<JsonException>(()=>JsonSerializer.Deserialize<ZeroPressPolicyData>("""{"OpeningAuto":false,"Rules":[]}"""),"missing opening mask");
Throws<JsonException>(()=>JsonSerializer.Deserialize<ZeroPressRule>("""{"UnitId":0,"Occurrence":1,"Mask":3}"""),"missing rule auto");
Console.WriteLine($"PASS: {tests} checks (synthetic events only; no game, HTTP, model or database)");
