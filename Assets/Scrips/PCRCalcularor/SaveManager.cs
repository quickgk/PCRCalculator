using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace PCRCaculator
{
    public static class SaveManager
    {
        private static Dictionary<string, string> objDict;

        private static string path
        {
            get
            {
#if PLATFORM_ANDROID
                return Application.persistentDataPath + "/存档.json";
#else
                return MainManager.GetSaveDataPath() + "/../../存档.json";
#endif
            }
        }

        private static void LoadAll()
        {
            try
            {
                objDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(path));
            }
            catch (Exception e)
            {
                objDict = new Dictionary<string, string>();
            }
        }

        private static void SaveAll()
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(objDict));
        }

        public static T Load<T>()
        {
            if (objDict == null) LoadAll();
            return JsonConvert.DeserializeObject<T>(objDict[typeof(T).FullName ?? nameof(T)]);
        }

        internal static void SaveAtomically<T>(T value)
        {
            if (objDict == null) LoadAll();
            var snapshot = new Dictionary<string, string>(objDict);
            snapshot[typeof(T).FullName ?? nameof(T)] = JsonConvert.SerializeObject(value);
            string target = Path.GetFullPath(path);
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot));
                if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
                objDict = snapshot;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static void Save<T>(T t)
        {
            objDict[typeof(T).FullName ?? nameof(T)] = JsonConvert.SerializeObject(t);
            SaveAll();
        }
    }
}