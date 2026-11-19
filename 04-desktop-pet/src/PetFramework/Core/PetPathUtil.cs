using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PetFramework
{
    /// <summary>
    /// 【配置文件的路径解析】—— 所有"放在 EXE 旁边"的配置文件都走这里。
    ///
    /// ⚠️ 为什么必须往【上一级】找：
    ///   编辑器里 Application.dataPath = &lt;工程&gt;/Assets，上一级正好是工程根目录；
    ///   而【成品里是 &lt;Build&gt;/MyPet_Data，上一级是 &lt;Build&gt;】。
    ///   不往上一级找的话，编辑器里能读到的配置，打成 EXE 就"消失"了
    ///   （文件在工程根目录，而 EXE 只在自己的 Build 目录里翻）。
    ///
    /// 查找顺序：① 显式指定的路径 ② EXE 同级 ③ EXE 上一级
    /// 都不存在 → 返回 EXE 同级（保存时会建在那儿，新用户第一次保存就落位）。
    /// </summary>
    public static class PetPathUtil
    {
        /// <summary>EXE 所在目录（编辑器里 = 工程根目录）。</summary>
        public static string ExeDir()
        {
            try
            {
                var d = Directory.GetParent(Application.dataPath);
                if (d != null) return d.FullName;
            }
            catch { }
            return Application.dataPath;
        }

        /// <summary>按"显式路径 → EXE 同级 → EXE 上一级"找文件；都不在就返回 EXE 同级。</summary>
        public static string ResolveBesideExe(string fileName, string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;
            return ResolveBesideExe(fileName);
        }

        public static string ResolveBesideExe(string fileName)
        {
            string name = string.IsNullOrEmpty(fileName) ? "config.json" : fileName;

            var cands = new List<string>();
            string exe = ExeDir();
            cands.Add(Path.Combine(exe, name));
            try
            {
                var up = Directory.GetParent(exe);
                if (up != null) cands.Add(Path.Combine(up.FullName, name));
            }
            catch { }

            for (int i = 0; i < cands.Count; i++)
                if (File.Exists(cands[i])) return cands[i];
            return cands[0];
        }
    }
}
