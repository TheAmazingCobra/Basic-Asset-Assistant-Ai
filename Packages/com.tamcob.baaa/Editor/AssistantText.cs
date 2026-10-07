#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // translations. the english text is the key, so a line nobody translated yet just shows in english.
    // the other languages are plain text files in the package's Languages folder, a translator can fix
    // those without touching code and a typo there cant break the build
    internal static class Lang
    {
        private const string PrefKey = "BAAA.Language";
        // the Languages folder has the same guid in every project, like the scripts do
        private const string FolderGuid = "0fd6dbe9a07b45be80bccee50a978cd1";
        private const string FolderPath = "Packages/com.tamcob.baaa/Languages";

        internal sealed class Language
        {
            public string Code = "en";
            public string Name = "English";
            public string Short = "";
            public string Open = "\"";
            public string Close = "\"";
            // what goes between list items, and between two sentences put together
            public string List = ", ";
            public string Space = " ";
            public CultureInfo Culture = CultureInfo.InvariantCulture;
            public readonly Dictionary<string, string> Lines = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static readonly Language EnglishOnly = new Language { Short = "English" };
        private static List<Language> _all;
        private static Language _now;
        private static int _english;

        public static string T(string en)
        {
            if (string.IsNullOrEmpty(en)) return en;
            Language l = Now;
            string line;
            return l != EnglishOnly && l.Lines.TryGetValue(en, out line) && line.Length > 0 ? line : en;
        }

        public static string T(string en, params object[] args)
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture, T(en), args);
            }
            catch (FormatException)
            {
                // a translation that lost one of its {0}s. english beats a crash
                return string.Format(CultureInfo.InvariantCulture, en, args);
            }
        }

        // marks text that gets translated later, like the tab names sitting in an array
        public static string N(string en)
        {
            return en;
        }

        public static Language Now
        {
            get
            {
                if (_english > 0) return EnglishOnly;
                if (_now == null) _now = Find(EditorPrefs.GetString(PrefKey, "en"));
                return _now;
            }
        }

        public static CultureInfo Culture
        {
            get { return Now.Culture; }
        }

        public static string Quote(string s)
        {
            Language l = Now;
            return l.Open + s + l.Close;
        }

        // ", " in most languages, "、" in japanese
        public static string List
        {
            get { return Now.List; }
        }

        // the space between two sentences. japanese doesnt put one after 。
        public static string Space
        {
            get { return Now.Space; }
        }

        // english first, then the files in name order
        public static List<Language> All()
        {
            Load();
            var list = new List<Language> { EnglishOnly };
            list.AddRange(_all);
            return list;
        }

        // the customers pick, kept for every project on this computer
        public static void Use(string code)
        {
            EditorPrefs.SetString(PrefKey, code);
            _now = Find(code);
        }

        // for the tests, so they dont change what the customer picked
        internal static void UseForNow(string code)
        {
            _now = Find(code);
        }

        // the project info is always in english, so whoever helps can read it
        public static IDisposable English()
        {
            _english++;
            return new EnglishScope();
        }

        private sealed class EnglishScope : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _english--;
            }
        }

        // after a language file changed
        public static void Reload()
        {
            string code = _now != null ? _now.Code : null;
            _all = null;
            _now = code != null ? Find(code) : null;
        }

        private static Language Find(string code)
        {
            Load();
            foreach (Language l in _all)
                if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return l;
            return EnglishOnly;
        }

        private static void Load()
        {
            if (_all != null) return;
            _all = new List<Language>();
            string folder = AssetDatabase.GUIDToAssetPath(FolderGuid);
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder)) folder = FolderPath;
            string full = Path.Combine(Path.GetDirectoryName(Application.dataPath), folder);
            if (!Directory.Exists(full)) return;
            foreach (string file in Directory.GetFiles(full, "*.txt"))
            {
                try
                {
                    Language l = Parse(File.ReadAllText(file));
                    if (l.Code != "en" && l.Lines.Count > 0) _all.Add(l);
                }
                catch (Exception)
                {
                    // a file that cant be read just isnt offered
                }
            }
            _all.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        // a header ("language: ...", "code: ...", "culture: ...", "quotes: ...") and then pairs of lines,
        // "en: " with the english and "tr: " with the translation. # starts a comment, \n is a new line
        internal static Language Parse(string text)
        {
            var l = new Language();
            string key = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1);
                if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
                switch (name)
                {
                    case "en":
                        key = value.Replace("\\n", "\n");
                        break;
                    case "tr":
                        if (key != null) l.Lines[key] = value.Replace("\\n", "\n");
                        key = null;
                        break;
                    case "language":
                        l.Name = value.Trim();
                        break;
                    case "short":
                        l.Short = value.Trim();
                        break;
                    case "code":
                        l.Code = value.Trim();
                        break;
                    case "culture":
                        try { l.Culture = CultureInfo.GetCultureInfo(value.Trim()); }
                        catch (CultureNotFoundException) { }
                        break;
                    case "quotes":
                        value = value.Trim();
                        if (value.Length == 2)
                        {
                            l.Open = value.Substring(0, 1);
                            l.Close = value.Substring(1, 1);
                        }
                        break;
                    // written as the characters themselves, "none" for nothing at all
                    case "list":
                        l.List = value.Trim() == "none" ? "" : value.TrimEnd('\r');
                        break;
                    case "space":
                        l.Space = value.Trim() == "none" ? "" : value.TrimEnd('\r');
                        break;
                }
            }
            if (l.Short.Length == 0) l.Short = l.Name;
            return l;
        }
    }
}
#endif
