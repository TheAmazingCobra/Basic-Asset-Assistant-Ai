#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace BAAA
{
    // the textures a material really draws with, for the VRAM numbers. Poiyomi keeps a texture in every
    // slot it was ever given (ramps and lookup tables of features that are off too) and drops the unused
    // ones itself when it locks the material for upload. so on a locked material whats left is whats used.
    // an unlocked one goes by the toggle each Thry section names and the "only in this mode" condition a
    // slot has, which is the same thing the lock looks at. other shaders count every texture they have
    internal static class TextureUse
    {
        private sealed class Slot
        {
            public int Id;
            public string Name;
            // the toggles of every section around the slot, and the conditions on it and its sections
            public string[] Toggles;
            public string[] Conditions;
        }

        private static readonly Dictionary<Shader, List<Slot>> Slots = new Dictionary<Shader, List<Slot>>();
        private static readonly Regex ToggleOf = new Regex(@"reference_property\s*:\s*(_\w+)", RegexOptions.CultureInvariant);
        private static readonly Regex ConditionOf = new Regex(@"condition_showS\s*:\s*(\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\))", RegexOptions.CultureInvariant);

        internal static IEnumerable<Texture> Of(Material m)
        {
            if (m == null || m.shader == null) yield break;
            bool locked = Poiyomi.IsLocked(m);
            foreach (Slot s in SlotsOf(m.shader))
            {
                Texture t = m.GetTexture(s.Id);
                if (t == null || (!locked && !On(m, s))) continue;
                yield return t;
            }
        }

        // whether the material would read the texture in this slot, if it had one
        internal static bool Uses(Material m, string prop)
        {
            if (m == null || m.shader == null) return false;
            if (Poiyomi.IsLocked(m)) return m.HasProperty(prop);
            foreach (Slot s in SlotsOf(m.shader))
                if (s.Name == prop) return On(m, s);
            return false;
        }

        private static bool On(Material m, Slot s)
        {
            foreach (string t in s.Toggles)
                if (m.HasProperty(t) && m.GetFloat(t) < 0.5f) return false;
            foreach (string c in s.Conditions)
                if (!Holds(m, c)) return false;
            return true;
        }

        // thry marks sections with m_start_x / m_end_x (s_ for sub sections, g_ for groups). a few end
        // markers are spelled different from their start, so they close whatever is open, by position
        private static List<Slot> SlotsOf(Shader shader)
        {
            List<Slot> list;
            if (Slots.TryGetValue(shader, out list)) return list;
            list = new List<Slot>();
            var toggles = new List<string>();
            var conditions = new List<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                string description = shader.GetPropertyDescription(i) ?? "";
                if (Marker(name, "start_"))
                {
                    toggles.Add(First(ToggleOf, description));
                    conditions.Add(First(ConditionOf, description));
                    continue;
                }
                if (Marker(name, "end_"))
                {
                    if (toggles.Count == 0) continue;
                    toggles.RemoveAt(toggles.Count - 1);
                    conditions.RemoveAt(conditions.Count - 1);
                    continue;
                }
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var mine = new List<string>(conditions.FindAll(c => c.Length > 0));
                string own = First(ConditionOf, description);
                if (own.Length > 0) mine.Add(own);
                list.Add(new Slot
                {
                    Id = shader.GetPropertyNameId(i),
                    Name = name,
                    Toggles = toggles.FindAll(t => t.Length > 0).ToArray(),
                    Conditions = mine.ToArray(),
                });
            }
            Slots[shader] = list;
            return list;
        }

        private static bool Marker(string name, string kind)
        {
            return name.Length > 2 + kind.Length && (name[0] == 'm' || name[0] == 's' || name[0] == 'g') && name[1] == '_'
                && string.CompareOrdinal(name, 2, kind, 0, kind.Length) == 0;
        }

        private static string First(Regex regex, string text)
        {
            Match match = regex.Match(text);
            return match.Success ? match.Groups[1].Value : "";
        }

        // thry's conditions, like (_LightingMode==3) or (_A==1&&(_B>0||_C!=2)). anything it can't read, or
        // a property the material doesn't have, counts as true so a texture is never left out by mistake
        private static bool Holds(Material m, string condition)
        {
            var reader = new ConditionReader { Text = condition, Material = m };
            try
            {
                bool value = reader.Or();
                reader.Skip();
                return reader.Unknown || reader.At != condition.Length || value;
            }
            catch (FormatException)
            {
                return true;
            }
        }

        private sealed class ConditionReader
        {
            public string Text;
            public Material Material;
            public int At;
            public bool Unknown;

            public bool Or()
            {
                bool value = And();
                while (Take("||")) value |= And();
                return value;
            }

            private bool And()
            {
                bool value = Unary();
                while (Take("&&")) value &= Unary();
                return value;
            }

            private bool Unary()
            {
                if (Take("!")) return !Unary();
                if (Take("("))
                {
                    bool inner = Or();
                    if (!Take(")")) throw new FormatException();
                    return inner;
                }
                string name = Word();
                string op = Take("==") ? "==" : Take("!=") ? "!=" : Take(">=") ? ">=" : Take("<=") ? "<=" : Take(">") ? ">" : Take("<") ? "<" : null;
                if (name.Length == 0 || op == null) throw new FormatException();
                float right = Number();
                if (!Material.HasProperty(name))
                {
                    Unknown = true;
                    return true;
                }
                float left = Material.GetFloat(name);
                switch (op)
                {
                    case "==": return Mathf.Abs(left - right) < 0.001f;
                    case "!=": return Mathf.Abs(left - right) >= 0.001f;
                    case ">=": return left >= right;
                    case "<=": return left <= right;
                    case ">": return left > right;
                    default: return left < right;
                }
            }

            public void Skip()
            {
                while (At < Text.Length && char.IsWhiteSpace(Text[At])) At++;
            }

            private bool Take(string token)
            {
                Skip();
                if (string.CompareOrdinal(Text, At, token, 0, token.Length) != 0) return false;
                At += token.Length;
                return true;
            }

            private string Word()
            {
                Skip();
                int start = At;
                while (At < Text.Length && (char.IsLetterOrDigit(Text[At]) || Text[At] == '_')) At++;
                return Text.Substring(start, At - start);
            }

            private float Number()
            {
                Skip();
                int start = At;
                while (At < Text.Length && (char.IsDigit(Text[At]) || Text[At] == '.' || Text[At] == '-' || Text[At] == '+')) At++;
                float value;
                if (!float.TryParse(Text.Substring(start, At - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new FormatException();
                return value;
            }
        }
    }
}
#endif
