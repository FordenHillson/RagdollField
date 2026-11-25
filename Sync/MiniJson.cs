using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace DeliriumRavenfield.Sync
{
    /// <summary>
    /// Minimal zero-dependency JSON parser & serializer for .NET Framework 4.7.1 (NDJSON Playroom Sync).
    /// </summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int idx = 0;
            return ParseValue(json, ref idx);
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            return Parse(json) as Dictionary<string, object>;
        }

        private static void SkipWs(string s, ref int i)
        {
            int len = s.Length;
            while (i < len)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObj(s, ref i);
            if (c == '[') return ParseArr(s, ref i);
            if (c == '"') return ParseStr(s, ref i);
            if (c == 't')
            {
                if (i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
                return null;
            }
            if (c == 'f')
            {
                if (i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
                return null;
            }
            if (c == 'n')
            {
                if (i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
                return null;
            }
            return ParseNum(s, ref i);
        }

        private static Dictionary<string, object> ParseObj(string s, ref int i)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; return dict; }
                string key = ParseStr(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                object val = ParseValue(s, ref i);
                if (key != null) dict[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return dict; }
            }
            return dict;
        }

        private static List<object> ParseArr(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; return list; }
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list; }
            }
            return list;
        }

        private static string ParseStr(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++; // '"'
            var sb = new StringBuilder();
            int len = s.Length;
            while (i < len)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i < len)
                {
                    char esc = s[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= len && int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static double ParseNum(string s, ref int i)
        {
            int start = i;
            int len = s.Length;
            while (i < len)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    i++;
                else
                    break;
            }
            if (i == start)
            {
                i++;
                return 0.0;
            }
            string tok = s.Substring(start, i - start);
            return double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
        }

        // Typed accessors
        public static string GetString(Dictionary<string, object> d, string k, string def = "")
        {
            if (d != null && d.TryGetValue(k, out var v) && v is string s) return s;
            return def;
        }

        public static bool GetBool(Dictionary<string, object> d, string k, bool def = false)
        {
            if (d != null && d.TryGetValue(k, out var v))
            {
                if (v is bool b) return b;
                if (v is double n) return Math.Abs(n) > 0.5;
            }
            return def;
        }

        public static float GetFloat(Dictionary<string, object> d, string k, float def = 0f)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is double n) return (float)n;
            return def;
        }

        public static bool TryGetFloat(Dictionary<string, object> d, string k, out float val)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is double n)
            {
                val = (float)n;
                return true;
            }
            val = 0f;
            return false;
        }

        public static int GetInt(Dictionary<string, object> d, string k, int def = 0)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is double n) return (int)Math.Round(n);
            return def;
        }

        public static long GetLong(Dictionary<string, object> d, string k, long def = 0)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is double n) return (long)Math.Round(n);
            return def;
        }

        public static uint GetUInt(Dictionary<string, object> d, string k, uint def = 0)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is double n) return (uint)Math.Max(0, Math.Round(n));
            return def;
        }

        public static List<object> GetList(Dictionary<string, object> d, string k)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is List<object> l) return l;
            return null;
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> d, string k)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is Dictionary<string, object> sub) return sub;
            return null;
        }

        public static float AsFloat(object o, float def = 0f) => o is double n ? (float)n : def;
        public static int AsInt(object o, int def = 0) => o is double n ? (int)Math.Round(n) : def;

        public static float[] GetFloatArray(Dictionary<string, object> d, string k)
        {
            var l = GetList(d, k);
            if (l == null) return null;
            var arr = new float[l.Count];
            for (int i = 0; i < l.Count; i++) arr[i] = AsFloat(l[i]);
            return arr;
        }

        public static Vector3 GetNmVec3Raw(Dictionary<string, object> d, string k)
        {
            var l = GetList(d, k);
            if (l == null || l.Count < 3) return Vector3.zero;
            return new Vector3(AsFloat(l[0]), AsFloat(l[1]), AsFloat(l[2]));
        }

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
