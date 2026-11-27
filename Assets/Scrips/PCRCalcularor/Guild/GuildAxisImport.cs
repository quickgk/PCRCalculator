#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SFB;
using UnityEngine;
using UnityEngine.UI;

namespace PCRCaculator.Guild
{
    public partial class GuildManager
    {
        private void AddAxisImportButton()
        {
            var canvas = AutoModeToggle.GetComponentInParent<Canvas>().rootCanvas;
            var excel = canvas.GetComponentsInChildren<Button>(true).FirstOrDefault(b =>
                Enumerable.Range(0, b.onClick.GetPersistentEventCount()).Any(i => b.onClick.GetPersistentMethodName(i) == nameof(LoadDataFromExcel)));
            if (excel == null) { Debug.LogWarning("未找到会战队伍菜单，无法添加图文轴入口。"); return; }
            if (excel.transform.parent.Find("导入图文轴") != null) return;
            var item = Instantiate(excel, excel.transform.parent, false);
            item.name = "导入图文轴";
            item.transform.SetSiblingIndex(excel.transform.GetSiblingIndex() + 1);
            item.GetComponent<RectTransform>().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 38);
            foreach (var t in item.GetComponentsInChildren<Text>(true)) t.text = item.name;
            item.onClick = new Button.ButtonClickedEvent();
            item.onClick.AddListener(() => ShowAxisDialog(canvas));
        }

        private GameObject axisDialog;
        private void ShowAxisDialog(Canvas canvas)
        {
            if (axisDialog != null) return;
            var ui = new DefaultControls.Resources();
            var overlay = axisDialog = DefaultControls.CreatePanel(ui);
            var lifetime = new CancellationTokenSource();
            void Place(GameObject obj, Transform parent, float x1, float y1, float x2, float y2)
            {
                var rect = (RectTransform)obj.transform;
                rect.SetParent(parent, false); rect.anchorMin = new Vector2(x1,y1); rect.anchorMax = new Vector2(x2,y2);
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                foreach (var t in obj.GetComponentsInChildren<Text>(true)) { t.font = characterGroupNameText.font; t.fontSize = 18; }
            }
            Place(overlay, canvas.transform, 0,0,1,1);
            overlay.name = "图文轴导入";
            overlay.GetComponent<Image>().color = new Color(0,0,0,.72f);
            var panel = DefaultControls.CreatePanel(ui);
            Place(panel, overlay.transform, .12f,.09f,.88f,.91f);
            panel.GetComponent<Image>().color = new Color(.13f,.15f,.20f,1);
            Text Label(string value, float l, float b, float r, float t)
            {
                var obj = DefaultControls.CreateText(ui); Place(obj,panel.transform,l,b,r,t);
                var label = obj.GetComponent<Text>(); label.text = value; label.color = Color.white;
                label.alignment = TextAnchor.MiddleLeft; return label;
            }
            InputField Field(string value, string hint, bool multi, float l, float b, float r, float t)
            {
                var obj = DefaultControls.CreateInputField(ui); Place(obj,panel.transform,l,b,r,t);
                var field = obj.GetComponent<InputField>(); field.text = value;
                field.lineType = multi ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
                field.textComponent.alignment = multi ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
                var placeholder = (Text)field.placeholder; placeholder.text = hint; placeholder.alignment = field.textComponent.alignment;
                return field;
            }
            Button ButtonAt(string caption, float l, float b, float r, float t, Action click)
            {
                var obj = DefaultControls.CreateButton(ui); Place(obj,panel.transform,l,b,r,t);
                obj.GetComponentInChildren<Text>().text = caption;
                var button = obj.GetComponent<Button>(); button.onClick.AddListener(() => click()); return button;
            }
            Label("导入完整操作轴", .03f,.88f,.37f,.98f);
            var input = Field(SettingData.GetCurrentPlayerGroup().importedAxisText ?? "", "粘贴文字，或从本机识别图片。每行：倒计时、角色、操作。", true,.03f,.23f,.97f,.86f);
            Label("目标队伍",.03f,.12f,.17f,.21f);
            var slot = Field((SettingData.currentPlayerGroupNum + 1).ToString(), "队伍号", false,.17f,.12f,.27f,.21f);
            slot.contentType = InputField.ContentType.IntegerNumber; slot.characterLimit = 4;
            var status = Label("已有队伍直接替换；未写月份时使用当前会战。",.03f,.01f,.98f,.12f);
            status.fontSize = 15;
            bool reading = false;
            async void ReadImage(bool clipboard)
            {
                if (reading) return;
                try
                {
                    string path = null;
                    if (!clipboard)
                    {
                        var files = StandaloneFileBrowser.OpenFilePanel("选择会战轴图", "", new[] { new ExtensionFilter("轴图", "png", "jpg", "jpeg") }, false);
                        if (files.Length == 0) return;
                        path = files[0];
                    }
                    reading = true; status.text = "正在使用本机中文 OCR 识别…";
                    var text = clipboard ? await GuildAxisImage.RecognizeClipboardAsync(lifetime.Token) : await GuildAxisImage.RecognizeFileAsync(path, lifetime.Token);
                    if (overlay != null) { input.text = text; status.text = text.Contains("【请核对") || text.Contains("【缺少") ? "请对照原图修正标记行后确定；目标队伍尚未改动。" : "识别完成，可直接确定；也可在这里修正文字。"; }
                }
                catch (Exception e) { if (overlay != null) status.text = e.Message; }
                finally { reading = false; }
            }
            void Close() { lifetime.Cancel(); Destroy(overlay); }
            ButtonAt("选图片",.55f,.89f,.74f,.98f,() => ReadImage(false));
            ButtonAt("粘贴图片",.76f,.89f,.97f,.98f,() => ReadImage(true));
            ButtonAt("取消",.59f,.12f,.76f,.21f,Close);
            ButtonAt("确定",.79f,.12f,.97f,.21f,() =>
            {
                if (reading) { status.text = "图片仍在识别，请稍候。"; return; }
                if (!int.TryParse(slot.text, out int number) || number < 1 || number > 9999) { status.text = "请输入有效的队伍号（从 1 开始）。"; return; }
                try { ImportAxisToTeam(input.text, number); }
                catch (Exception e) { status.text = e.Message; return; }
                Close(); MainManager.Instance.WindowMessage($"已存入队伍 {number}");
            });
        }

        private void ImportAxisToTeam(string text, int number)
        {
            var app = MainManager.Instance;
            var current = SettingData.GetCurrentPlayerGroup();
            var e = GuildAxisText.GetEncounter(text);
            int month = e.month > 0 ? e.month : current.currentGuildMonth;
            if (e.month > 0 && month != current.currentGuildMonth && e.boss == 0) throw new FormatException("更换会战月份时请指明阶段和 BOSS，例如 D2。");
            int enemyId = current.selectedEnemyIDs?.FirstOrDefault() ?? 0;
            if (e.boss != 0)
            {
                int stage = month >= 1067 && e.phase == 1 ? 0 : e.phase;
                if (!guildEnemyDatas.TryGetValue(month, out var battle) || stage >= battle.enemyIds.Length ||
                    battle.enemyIds[stage] == null || e.boss > battle.enemyIds[stage].Length || battle.enemyIds[stage][e.boss-1] <= 0)
                    throw new FormatException("当前会战没有图中的阶段/BOSS；请先选对月份或注明“会战号：…”。");
                enemyId = battle.enemyIds[stage][e.boss-1];
            }
            var bossNames = new List<string>();
            if (EnemyDataDic.TryGetValue(enemyId, out var boss))
            {
                bossNames.Add(boss.name); bossNames.Add((boss.name ?? "").Replace("本体", ""));
                if (boss.skillData != null && app.SkillDataDic.TryGetValue(boss.skillData.UB, out var ub)) bossNames.Add(ub.name);
            }
            var index = new GuildAxisText.NameIndex();
            var names = index.ids;
            var aliases = JsonConvert.DeserializeObject<Dictionary<int,List<string>>>(File.ReadAllText(Path.Combine(Application.streamingAssetsPath,"Datas","chara_name.json")));
            foreach (var entry in app.unitDataDic)
            {
                int id = entry.Key;
                if (id < 100000 || id >= 200000 || !app.UnitRarityDic.ContainsKey(id)) continue;
                index.Add(id.ToString(), id, 4); index.Add(entry.Value.GetUnitNameInternal(), id, 1);
                if (app.TryGetConfiguredUnitNickname(id, out var configured)) index.Add(configured, id, 3);
                if (aliases.TryGetValue(id/100, out var list)) foreach (var alias in list) index.Add(alias, id, 2);
            }
            if (names.TryGetValue("原花", out int flower) && flower > 0) index.Add("a花", flower, 2);
            int Skill(int id, string hint)
            {
                string Clean(string s) => GuildAxisText.Key(s ?? "").TrimEnd('+','!','?','。');
                var ids = app.UnitRarityDic[id].GetSkillList(app.unitDataDic[id]).Where(n => n > 0 &&
                    ((app.SkillDataDic.TryGetValue(n, out var sk) && Clean(sk.name) == Clean(hint)) ||
                     (app.SkillNameAndDescribe_cn.TryGetValue(n, out var cn) && cn.Length > 0 && Clean(cn[0]) == Clean(hint)))).Distinct().ToArray();
                return ids.Length == 1 ? ids[0] : 0;
            }
            var parsed = GuildAxisText.Parse(text, names, id => app.UnitRarityDic[id].detailData.searchAreaWidth, bossNames, Skill);
            var group = JsonConvert.DeserializeObject<GuildPlayerGroupData>(JsonConvert.SerializeObject(current));
            group.playerData = new AddedPlayerData { playerName = (e.boss == 0 ? "图文轴" : $"{(char)('A'+e.phase-1)}{e.boss}") + " · " + string.Join("/",parsed.units.Select(app.GetUnitNickName)) };
            if (!string.IsNullOrWhiteSpace(parsed.title)) group.playerData.playerName = parsed.title.Substring(0, Math.Min(parsed.title.Length,80));
            for (int i=0; i<parsed.units.Length; i++)
            {
                int id = parsed.units[i];
                var template = current.playerData?.playrCharacters?.FirstOrDefault(p => p.unitId == id) ?? app.unitDataDic[id];
                var unit = JsonConvert.DeserializeObject<UnitData>(JsonConvert.SerializeObject(template));
                int col = 4-i; // The five growth columns follow the portraits: back -> front.
                if (parsed.growth.TryGetValue("等级", out var levels))
                {
                    int level = int.Parse(levels[col]);
                    if (level <= 0 || level > 999) throw new FormatException("等级无效：" + level);
                    bool blank = unit.level == 1 && unit.rank == 1 && unit.skillLevel.Take(3).All(n => n == 0);
                    for (int k=0;k<unit.skillLevel.Length;k++) unit.skillLevel[k] = blank || unit.skillLevel[k] >= unit.level ? level : Math.Min(level,unit.skillLevel[k]);
                    unit.level = level;
                }
                if (parsed.growth.TryGetValue("星级", out var stars))
                {
                    var choices = stars[col].Split('/').Select(int.Parse).ToArray();
                    if (choices.Length == 1) unit.rarity = choices[0];
                    else if (!choices.Contains(unit.rarity)) throw new FormatException($"{app.GetUnitNickName(id)}有多个星级方案，请把星级改为一个明确值。");
                    if (unit.rarity > unit.GetMaxRarity()) throw new FormatException($"{app.GetUnitNickName(id)}不能使用这个星级。");
                }
                if (parsed.growth.TryGetValue("rank", out var ranks) && !string.Equals(unit.GetRankDescribe(), ranks[col], StringComparison.OrdinalIgnoreCase))
                {
                    int[] rank = ranks[col].Substring(1).Split('-').Select(int.Parse).ToArray();
                    if (rank[0]<1 || rank[0]>app.PlayerSetting.GetMaxRank() || !(rank[1]==0 || rank[1]==3 || rank[1]==5 || rank[1]==6)) throw new FormatException("不能唯一确定六个装备槽：" + ranks[col]);
                    unit.rank = rank[0];
                    var equipment = app.UnitRarityDic[id].GetRankEquipments(unit.rank);
                    for (int k=0;k<6;k++)
                    {
                        bool wears = rank[1]==6 || rank[1]==5 && k!=0 || rank[1]==3 && k%2==1;
                        if (wears && !app.EquipmentDic.ContainsKey(equipment[k])) throw new FormatException("本地不存在所需装备："+ranks[col]);
                        unit.equipLevel[k] = wears ? app.EquipmentDic[equipment[k]].GetMaxLevel() : -1;
                    }
                }
                if (parsed.growth.TryGetValue("专武",out var equips))
                {
                    var pair = equips[col].Split('/'); unit.uniqueEqLv = pair[0]=="无" ? -1 : int.Parse(pair[0]);
                    unit.uniqueEq2Lv = pair.Length==2 && pair[1]!="无" ? int.Parse(pair[1]) : -1;
                    unit.uniqueEqPlus = unit.uniqueEq2Plus = false;
                }
                group.playerData.playrCharacters.Add(unit);
                foreach (var cue in parsed.plan.cues.Where(c => c.triggerSlot == i && c.skillId > 0))
                {
                    var rarity = app.UnitRarityDic[id];
                    int skillSlot = Array.IndexOf(rarity.GetSkillList(app.unitDataDic[id]), cue.skillId);
                    cue.skillId = rarity.GetSkillList(unit)[skillSlot];
                }
            }
            GuildAxisGrowth.Apply(parsed, group, app);
            if (e.boss != 0)
            {
                group.currentGuildMonth = month; group.currentGuildEnemyNum = e.boss-1; group.currentTurn = e.phase;
                group.selectedEnemyIDs = new[] { enemyId }; group.isSpecialBoss = false; ClearLabyrinthSelection(group); group.waveGroupId = 0;
            }
            group.UBExecTimeData = Enumerable.Range(0,6).Select(_ => new List<float>()).ToList();
            group.SemanUBExecTimeData = Enumerable.Range(0,6).Select(_ => new List<int>()).ToList();
            group.SemanUBSequenceData = new List<int>();
            group.useSequenceUB = group.useSemanMode = group.useSetMode = group.useAutoMode = false;
            group.importedAxis = parsed.plan; group.importedAxisText = text;
            var updated = SettingData.WithImportedTeam(number, group);
            SaveManager.SaveAtomically(updated);
            staticsettingData = updated;
            if (SettingData.currentPlayerGroupNum == number-1)
            {
                AutoModeToggle.SetIsOnWithoutNotify(false); SetModeToggle.SetIsOnWithoutNotify(false);
                SemanModeToggle.SetIsOnWithoutNotify(false); SequenceUBToggle?.SetIsOnWithoutNotify(false); Refresh();
            }
        }
    }
}
#endif
