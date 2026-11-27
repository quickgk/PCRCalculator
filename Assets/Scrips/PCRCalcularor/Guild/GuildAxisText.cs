using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PCRCaculator.Guild
{
    // Slot numbers in the executable plan follow the game's formation, not the order of OCR tokens.
    internal static class GuildAxisText
    {
        internal sealed class NameIndex
        {
            internal readonly Dictionary<string,int> ids = new Dictionary<string,int>();
            private readonly Dictionary<string,int> ranks = new Dictionary<string,int>();
            internal void Add(string name, int id, int rank)
            {
                if (string.IsNullOrWhiteSpace(name) || id < 100000 || id >= 200000) return;
                string key = Key(name);
                if (!ranks.TryGetValue(key,out int old) || rank > old) { ranks[key]=rank; ids[key]=id; }
                else if (rank==old && ids[key]!=id) ids[key]=-1;
            }
        }

        internal sealed class Result
        {
            internal int[] units;
            internal string title;
            internal int phase, boss, month;
            internal GuildAxisPlan plan = new GuildAxisPlan();
            internal readonly Dictionary<string, string[]> growth = new Dictionary<string, string[]>();
        }

        internal static string Key(string s) => Regex.Replace(s.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"\s+", "")
            .Replace('—', '-').Replace('–', '-').Replace('→', '-').Replace('×', 'x');

        private const string Clock = @"(?:[01]:[0-5]\d|\d{1,3})";
        private static readonly Regex At = new Regex("^(" + Clock + @")(?![\d./])[:|]?(.*)$");
        private static readonly Regex Encounter = new Regex(@"(?:^|均伤|boss:|会战:)([a-e])([1-5])(?!\d)");
        private static readonly Regex Switches = new Regex(@"^(?:set[:=]?)?([ox〇○0。]{5})?(?:[|,;]?(开|关)(?:auto|自动))?$");

        internal static (int phase, int boss, int month) GetEncounter(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 120000) throw new FormatException("请提供一份完整的操作轴文字（不超过 12 万字）。");
            int phase = 0, boss = 0, month = 0;
            foreach (var raw in source.Split('\n'))
            {
                var text = Key(raw);
                var m = Encounter.Match(text);
                if (m.Success)
                {
                    int p = m.Groups[1].Value[0] - 'a' + 1, b = m.Groups[2].Value[0] - '0';
                    if (boss != 0 && (phase != p || boss != b)) throw new FormatException("一次只能导入一条轴，检测到不同的 BOSS。");
                    phase = p; boss = b;
                }
                var id = Regex.Match(text, @"^(?:会战号|会战编号)[:=]?(\d{4})$");
                if (id.Success)
                {
                    int value=int.Parse(id.Groups[1].Value);
                    if (month!=0 && month!=value) throw new FormatException("一份轴中出现了不同的会战号。");
                    month=value;
                }
                else if (text.StartsWith("会战号") || text.StartsWith("会战编号")) throw new FormatException("会战号应为四位数字。");
            }
            return (phase, boss, month);
        }

        private static int Second(string token)
        {
            int n = int.Parse(token.Replace(":", ""));
            if (token.Contains(":") || token.Length == 3)
            {
                if (n % 100 > 59) throw new FormatException("无效倒计时：" + token);
                n = n / 100 * 60 + n % 100;
            }
            if (n > 90) throw new FormatException("倒计时超出 1:30：" + token);
            return n;
        }

        internal static Result Parse(string source, IDictionary<string, int> names, Func<int, int> position,
            IEnumerable<string> bossNames, Func<int, string, int> skillId = null)
        {
            var result = new Result();
            (result.phase, result.boss, result.month) = GetEncounter(source);
            var plan = result.plan;
            var roster = new List<int>();
            var used = new HashSet<int>();
            var plainRows = new HashSet<int>();
            var once = new Dictionary<GuildAxisCue, int>();
            var aliases = names.Keys.Where(s => s.Length != 0).OrderByDescending(s => s.Length).ToArray();
            var boss = new HashSet<string>(bossNames.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Key)) { "boss", "bossub", "bub", "王ub" };
            int line = 0, last = 90;
            bool body = false, opening = false, ended = false;
            FormatException Bad(string message) => new FormatException($"第 {line} 行：{message}");
            int Actor(ref string tail)
            {
                var text = tail;
                var alias = aliases.FirstOrDefault(a => text.StartsWith(a, StringComparison.Ordinal));
                if (alias == null || names[alias] <= 0) throw Bad("角色无法唯一识别：" + text);
                tail = tail.Substring(alias.Length);
                return names[alias];
            }
            void Anchor(int value)
            {
                if (value < 0) return;
                if (value > last) throw Bad("倒计时或行顺序不符。");
                last = value;
            }
            (int mask, int auto) State(string text)
            {
                var m = Switches.Match(text.Trim(',', '、', '|', ';'));
                if (!m.Success || !m.Groups[1].Success && !m.Groups[2].Success)
                    throw Bad("无法识别完整开关操作：" + text);
                int mask = -1;
                if (m.Groups[1].Success)
                {
                    mask = 0;
                    foreach (var c in m.Groups[1].Value) mask = (mask << 1) | (c == 'x' ? 0 : 1);
                }
                return (mask, !m.Groups[2].Success ? -1 : m.Groups[2].Value == "开" ? 1 : 0);
            }
            void ReadTeam(string tail, bool explicitTeam)
            {
                var ids = new List<int>();
                tail = tail.TrimStart(':', '-', '|');
                while (tail.Length != 0 && ids.Count < 5)
                {
                    string rest = tail;
                    if (!aliases.Any(a => rest.StartsWith(a, StringComparison.Ordinal))) break;
                    ids.Add(Actor(ref tail)); tail = tail.TrimStart(',', '、', '/', '-', '|', ';');
                }
                if (!explicitTeam && ids.Count == 0) return;
                if (ids.Distinct().Count() != 5 || explicitTeam && tail.Length > 0)
                    throw Bad("阵容必须有五个不同的角色；请用“阵容：甲、乙、丙、丁、戊”指明。");
                if (roster.Count > 0 && !new HashSet<int>(roster).SetEquals(ids)) throw Bad("检测到两套不同的阵容。");
                roster = ids;
            }
            string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int heading = Array.FindIndex(lines, s => Key(s).Trim('|').StartsWith("秒数"));
            foreach (string raw in lines)
            {
                line++;
                string text = Key(raw).Trim('|');
                if (text.Length == 0) continue;
                if (text.Contains("【请核对") || text.Contains("【缺少") || text.Contains("【该行")) throw Bad("图片识别不完整，请对照图片修正：“" + raw.Trim() + "”。");
                if (text.StartsWith("阵容:") || text.StartsWith("队员:")) { ReadTeam(text.Substring(3), true); continue; }
                var title = Encounter.Match(text);
                if (!body && title.Success) { result.title = raw.Trim(); ReadTeam(text.Substring(title.Index + title.Length), false); continue; }
                if (text.StartsWith("秒数") || text.StartsWith("时间角色")) { body = true; continue; }
                if (text == "timeup" || text == "结束") { ended = true; continue; }
                if (text.StartsWith("备注:") || text.StartsWith("说明:")) continue;
                if (ended) throw Bad("TIME UP 后仍有未识别的内容。");
                if (text.StartsWith("开局") || text.StartsWith("开场"))
                {
                    if (opening || plan.cues.Count != 0) throw Bad("开场状态重复或不在第一条。");
                    var state = State(text.Substring(2).TrimStart(':', '|'));
                    plan.openingMask = Math.Max(0, state.mask); plan.openingAuto = state.auto == 1;
                    opening = body = true; continue;
                }
                if (!body && ReadGrowth(raw, result, line)) continue;
                if (!body && heading >= line) continue;
                var rowText = Key(Regex.Replace(raw.Trim(), @"^([01]:[0-5]\d|\d{1,3})\s+(?=\d{6}(?!\d))", "$1|").Replace('\t','|')).Trim('|');
                var row = At.Match(rowText);
                if (!row.Success)
                {
                    if (body || aliases.Any(a => text.StartsWith(a,StringComparison.Ordinal)) || text.Contains("(auto)") || text.Contains("目押")) throw Bad("缺少倒计时或不是完整操作行：" + raw.Trim());
                    continue;
                }
                int sec = Second(row.Groups[1].Value);
                var action = row.Groups[2].Value.Trim('|', ':');
                if (action.Length == 0) throw Bad("有时间但没有操作。");
                Anchor(sec);
                body = true;
                // Notes describing a tolerance are not a frame number to press on.
                action = Regex.Replace(action, @"[,|;]*(?:\()?容错\d+(?:帧|f)", "");
                action = Regex.Replace(action, @"[,|;]*(?:\(\d+f开始\))?太慢会扣\d+[-~至]\d+w蓝字$", "");
                if (Switches.IsMatch(action))
                {
                    var state=State(action);
                    plan.cues.Add(new GuildAxisCue { kind=GuildAxisCueKind.Control, trigger=GuildAxisTrigger.RemainingSecond, line=line, remainingSecond=sec, afterMask=state.mask, afterAuto=state.auto });
                    continue;
                }
                var marker = boss.OrderByDescending(s => s.Length).FirstOrDefault(s => action.StartsWith(s, StringComparison.Ordinal));
                if (marker != null)
                {
                    var cue = new GuildAxisCue { kind = GuildAxisCueKind.Boss, line = line, remainingSecond = sec };
                    var op = action.Substring(marker.Length).Trim(':', '|');
                    if (op.Length != 0) (cue.afterMask, cue.afterAuto) = State(op);
                    plan.cues.Add(cue); continue;
                }
                // Explicit switch tables describe a naturally occurring UB, not a request to tap the actor.
                string tail = action;
                int first = Actor(ref tail); used.Add(first);
                tail = tail.TrimStart('|', ':');
                if (tail.Length == 0) plainRows.Add(line);
                if (tail.StartsWith("开一次") || (tail.Length != 0 && Switches.IsMatch(tail)))
                {
                    var cue = new GuildAxisCue { kind = GuildAxisCueKind.Observe, slot = first, line = line, remainingSecond = sec };
                    if (tail.StartsWith("开一次"))
                    {
                        tail = tail.Substring(3);
                        int id = Actor(ref tail); used.Add(id); once.Add(cue, id);
                        (cue.deferredMask, cue.afterAuto) = State(tail);
                        if (cue.deferredMask < 0 || cue.afterAuto != -1) throw Bad("开一次后需要明确的五位最终 SET 状态。");
                    }
                    else (cue.afterMask, cue.afterAuto) = State(tail);
                    plan.cues.Add(cue); continue;
                }
                // A window may name alternatives. Only use an explicitly offered, observable game event.
                if (tail.Contains("开auto") || tail.Contains("普攻后开") || tail.Contains("目押") || tail.Contains("目压") || Regex.IsMatch(tail,@"后(?:按|点)(?:ub)?\)?$"))
                {
                    string cond = Regex.Replace(tail, @"^auto\(|^\(", "").TrimEnd(')');
                    var cue = new GuildAxisCue { kind = tail.Contains("auto") ? GuildAxisCueKind.Auto : GuildAxisCueKind.Repeat, line = line, slot = first, remainingSecond = sec };
                    var deadline = Regex.Match(cond, @"\|([^|,]+?)(?:平a|普攻)快命中[,;]最晚(?:要)?在命中时开出(?:[,;]慢了炸刀)?$");
                    if (deadline.Success)
                    {
                        if (!names.TryGetValue(deadline.Groups[1].Value,out int endAt) || endAt <= 0) throw Bad("无法确定普攻命中上限的角色。");
                        cue.deadlineNormalSlot=endAt; used.Add(endAt);
                        cond=cond.Substring(0,deadline.Index);
                    }
                    if (cond.Contains("|")) throw Bad("有未解析的动作或说明："+cond);
                    bool matched = false;
                    foreach (var alternative in cond.Split('/'))
                    {
                        cue.skillId = 0;
                        var choice = Regex.Replace(alternative, @"^(?:卡帧)?(?:目押|目压|押)", "");
                        var m = Regex.Match(choice, @"^(.+?)(?:后开(?:auto)?|后(?:按|点)(?:ub)?|后目押|后|开auto)$");
                        if (!m.Success) continue;
                        string whoAndSkill = m.Groups[1].Value.TrimEnd('|');
                        var key = aliases.FirstOrDefault(a => whoAndSkill.StartsWith(a, StringComparison.Ordinal));
                        if (key != null && names[key] <= 0) continue;
                        int who = key == null ? first : names[key];
                        var skill = key == null ? whoAndSkill : whoAndSkill.Substring(key.Length);
                        if (skill == "普攻" || skill == "平a") cue.trigger = GuildAxisTrigger.NormalAttackEffect;
                        else if (skill == "a抬手" || skill == "普攻抬手" || skill == "平a抬手") cue.trigger = GuildAxisTrigger.NormalAttackStart;
                        else
                        {
                            if (skillId == null || (cue.skillId = skillId(who, skill)) <= 0) continue;
                            cue.trigger = GuildAxisTrigger.SkillEffect;
                        }
                        cue.triggerSlot = who; used.Add(who); matched = true; break;
                    }
                    if (!matched) throw Bad("不能唯一定位目押的动作或技能：" + tail);
                    plan.cues.Add(cue); continue;
                }
                string chain = action;
                bool firstStep = true;
                while (chain.Length != 0)
                {
                    chain = chain.TrimStart('-', '·', '.', '、', ',', '|');
                    if (chain.Length == 0) throw Bad("出手链缺少最后一个角色。");
                    int refSec = firstStep ? sec : -1; firstStep = false;
                    var tag = At.Match(chain);
                    if (tag.Success && tag.Groups[2].Value.Length != 0) { refSec = Second(tag.Groups[1].Value); chain = tag.Groups[2].Value; }
                    int id = Actor(ref chain); used.Add(id);
                    var suffix = Regex.Match(chain, "^\\((" + Clock + ")\\)");
                    if (suffix.Success) { refSec = Second(suffix.Groups[1].Value); chain = chain.Substring(suffix.Length); }
                    var method = Regex.Match(chain, @"^(?:\((auto|连点)\)|(auto|连点))");
                    bool auto = method.Success && method.Value.Contains("auto");
                    if (method.Success) chain = chain.Substring(method.Length);
                    Anchor(refSec);
                    plan.cues.Add(new GuildAxisCue { kind = auto ? GuildAxisCueKind.Auto : GuildAxisCueKind.Repeat, line = line, slot = id, remainingSecond = refSec });
                }
            }
            if (plan.cues.Count == 0) throw new FormatException("没有可执行的操作行。");
            if (opening && plan.cues.Any(c => c.kind == GuildAxisCueKind.Observe))
                foreach (var c in plan.cues.Where(c => c.kind == GuildAxisCueKind.Repeat && plainRows.Contains(c.line))) c.kind = GuildAxisCueKind.Observe;
            if (roster.Count == 0) roster = used.ToList();
            if (roster.Count != 5 || !used.IsSubsetOf(roster)) throw new FormatException("角色与五人阵容不符；请核对标题、昵称或补上“阵容：…”一行。");
            result.units = roster.OrderBy(position).ThenBy(id => id).ToArray();
            plan.unitIds = result.units;
            foreach (var cue in plan.cues)
            {
                if (cue.slot >= 0) cue.slot = Array.IndexOf(result.units, cue.slot);
                if (cue.deadlineNormalSlot >= 0) cue.deadlineNormalSlot = Array.IndexOf(result.units, cue.deadlineNormalSlot);
                if (cue.triggerSlot >= 0) cue.triggerSlot = Array.IndexOf(result.units, cue.triggerSlot);
                if (once.TryGetValue(cue, out int actor)) cue.afterOnceMask = 1 << Array.IndexOf(result.units, actor);
            }
            var error = GuildAxisPlayback.Validate(plan, 5);
            if (error != null) throw new FormatException(error);
            return result;
        }

        private static bool ReadGrowth(string raw, Result result, int line)
        {
            string s = raw.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant().Replace('–','-').Replace('—','-');
            string compact = Key(s).Replace('【','[').Replace('】',']');
            var point = Regex.Match(compact, @"第(\d+)页第(\d+)合流交点\[(\d+)级\]");
            var master = Regex.Match(compact, @"第(\d+)个");
            if (point.Success) result.growth["属性技能"] = new[]{point.Groups[1].Value,point.Groups[2].Value,point.Groups[3].Value};
            if (master.Success) result.growth["大师技能"] = new[]{master.Groups[1].Value};
            if (point.Success || master.Success) return true;
            if (compact.StartsWith("属性技能") || compact.StartsWith("大师技能")) throw new FormatException($"第 {line} 行：无法确定技能树终点。");
            const string pair = @"\d+\s*[-·.]\s*\d+\s*/\s*\d+\s*[-·.]\s*\d+";
            var m = Regex.Match(s, @"^(等级|rank|星级(?:\s*\([^)]*\))?|专一\s*/\s*专二|专1\s*/\s*专2|装备\s*ex|武\s*/\s*防\s*/\s*饰|属性等级|职阶上|职阶下|职阶专精|职介)[\s:|]*(.*)$");
            if (!m.Success && Regex.Matches(s,pair).Count < 5) return false;
            var label = m.Success ? m.Groups[1].Value : "职阶";
            string key = label.StartsWith("星级") ? "星级" : label.StartsWith("专") ? "专武" : label.StartsWith("装备") || label.StartsWith("武") ? "ex" : label;
            if (label.StartsWith("职") && label != "职阶上" && label != "职阶下") key = result.growth.ContainsKey("职阶上") ? "职阶下" : "职阶上";
            string pattern = key == "rank" ? @"r\s*\d+\s*[-.·]\s*\d+" : key == "星级" ? @"[1-6](?:\s*/\s*[1-6])?" : key == "专武" ? @"(?:无|\d+)(?:\s*/\s*(?:无|\d+))?" : key=="ex" ? @"(?:[sgpr][0-5]|-)(?:\s*/\s*(?:[sgpr][0-5]|-)){2}" : key=="属性等级" ? @"[火水风光暗]\s*\d+" : key.StartsWith("职阶") ? pair : @"\d+";
            string cells = m.Success ? m.Groups[2].Value : s;
            if (string.IsNullOrWhiteSpace(cells) && key.StartsWith("职阶")) return false;
            var values = Regex.Matches(cells,pattern).Cast<Match>().Select(v => Key(v.Value).Replace('·','-').Replace('.','-')).ToArray();
            if (values.Length != 5 || Regex.Replace(cells,pattern,"").Trim(' ','\t','|',',','、',';').Length != 0) throw new FormatException($"第 {line} 行：{key}必须恰好有五个明确数值。");
            if (result.growth.TryGetValue(key,out var previous) && !previous.SequenceEqual(values)) throw new FormatException($"第 {line} 行：{key}有两组不同的数值。");
            result.growth[key] = values;
            return true;
        }
    }
}
