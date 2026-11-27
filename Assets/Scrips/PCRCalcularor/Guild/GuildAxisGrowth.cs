#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Linq;
using Elements;

namespace PCRCaculator.Guild
{
    internal static class GuildAxisGrowth
    {
        // Source columns follow the portraits (back to front); the player list is front to back.
        internal static void Apply(GuildAxisText.Result source, GuildPlayerGroupData group, MainManager app)
        {
            var cells = source.growth;
            bool top = cells.TryGetValue("职阶上", out var upper), bottom = cells.TryGetValue("职阶下", out var lower);
            if (top != bottom) throw new FormatException("职阶需要上下两行完整数值。");
            var levels = new Dictionary<int,int>();
            var mastery = new Dictionary<int,(int,int)>();
            for (int i=0; i<source.units.Length; i++)
            {
                var unit = group.playerData.playrCharacters[i];
                int id = unit.unitId, col = 4-i;
                if (cells.TryGetValue("ex", out var ex))
                {
                    var parts = ex[col].Split('/');
                    if (!app.UnitExEquips.TryGetValue(id, out var slots) || slots.Length != 3) throw new FormatException("未找到 " + app.GetUnitNickName(id) + " 的 EX 槽。");
                    for (int s=0; s<3; s++)
                    {
                        var part = parts[s];
                        if (part == "-") { unit.exEquip[s]=0; unit.exEquipLevel[s]=0; continue; }
                        int rarity = "sgpr".IndexOf(part[0])+2;
                        var valid = slots[s].Where(e => e.Value.clan_battle_equip_flag && e.Value.rarity == rarity).Select(e=>e.Key).ToList();
                        if (!valid.Contains(unit.exEquip[s]))
                        {
                            if (valid.Count != 1) throw new FormatException($"{app.GetUnitNickName(id)} 第 {s+1} 格 EX 无法由 {part} 唯一确定，请先在当前队伍中选好具体装备。");
                            unit.exEquip[s] = valid[0];
                        }
                        unit.exEquipLevel[s] = int.Parse(part.Substring(1));
                    }
                }
                if (cells.TryGetValue("属性等级",out var attrs))
                {
                    int type = "火水风光暗".IndexOf(attrs[col][0])+1, level=int.Parse(attrs[col].Substring(1));
                    if (!app.GetTalentIds(id).Contains(type) || levels.TryGetValue(type,out int other) && other != level) throw new FormatException("属性等级与本队角色不一致："+attrs[col]);
                    levels[type]=level;
                }
                if (top)
                {
                    int role=app.UnitRarityDic[id].detailData.unit_role_id;
                    var slots=app.UnitRoleMasteryIdDic.Values.Where(x=>x.unit_role_id==role).OrderBy(x=>x.slot_id).ToArray();
                    if (slots.Length!=4) throw new FormatException("未找到四个职阶槽："+app.GetUnitNickName(id));
                    var spec=upper[col].Split('/').Concat(lower[col].Split('/')).ToArray();
                    for (int s=0; s<4; s++)
                    {
                        var pair=spec[s].Split('-').Select(int.Parse).ToArray(); var value=(pair[0],pair[1]); int mid=slots[s].mastery_id;
                        if (mastery.TryGetValue(mid,out var other) && other!=value) throw new FormatException("同职阶的数值不一致："+app.GetUnitNickName(id));
                        if (pair[0] != 0 && (!app.UnitRoleMasterySlotEnhanceDataDic.TryGetValue(mid,out var info) || !info.slot_level_info.TryGetValue(pair[0],out var slot) || !slot.enhance_level_info.ContainsKey(pair[1]))) throw new FormatException("本地不存在该职阶："+spec[s]);
                        mastery[mid]=value;
                    }
                }
            }
            group.talentLevel ??= new Dictionary<int,int>(); group.roleMastery ??= new Dictionary<int,(int,int)>();
            foreach (var entry in levels) group.talentLevel[entry.Key]=entry.Value;
            foreach (var entry in mastery) group.roleMastery[entry.Key]=entry.Value;
            if (cells.TryGetValue("属性技能",out var nodes))
            {
                int page=int.Parse(nodes[0]), join=int.Parse(nodes[1]), lv=int.Parse(nodes[2]);
                var matches=app.TalentSkillNodeDic.Values.Where(n=>n.page_num==page && n.pre_node_ids!=null && n.pre_node_ids.Where(p=>p>0).Distinct().Count()>1).OrderBy(n=>n.node_id).ToArray();
                if (join<1 || join>matches.Length || lv<1 || lv>(matches[join-1].enhance_level_ids?.Count ?? 0)) throw new FormatException("当前数据库没有图中的属性技能交点或等级。");
                group.talentSkill=TeamEnhanceParameter.ExpandTalentSkillLevels(new Dictionary<int,int>{{matches[join-1].node_id,lv}},app.TalentSkillNodeDic);
            }
            if (cells.TryGetValue("大师技能",out var master))
            {
                int id=int.Parse(master[0]);
                if (!app.TeamSkillNodeDic.TryGetValue(id,out var n)) throw new FormatException("当前数据库没有大师技能："+id);
                int lv=n.enhance_level_ids?.Count ?? 0;
                if (lv == 0) throw new FormatException("该大师技能没有可用等级："+id);
                group.teamSkill=TeamEnhanceParameter.ExpandTeamSkillLevels(new Dictionary<int,int>{{id,lv}},app.TeamSkillNodeDic);
            }
        }
    }
}
#endif
