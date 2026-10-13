using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TiaInspector
{
    /// <summary>
    /// 최소 JSON 직렬화기. JavaScriptSerializer 를 쓰지 않는 이유는 두 가지다 —
    /// 들여쓰기가 없고, 대형 덤프에서 MaxJsonLength 에 걸린다.
    /// 입력은 Dictionary(string,object) / IEnumerable / 기본형만 다룬다.
    /// </summary>
    internal static class Json
    {
        public static string Serialize(object value, bool indent = true)
        {
            var sb = new StringBuilder(1024);
            Write(sb, value, indent ? 0 : -1);
            return sb.ToString();
        }

        public static void WriteFile(string path, object value)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // BOM 없는 UTF-8 — 외부 파서(python 등)가 그대로 읽을 수 있게.
            File.WriteAllText(path, Serialize(value), new UTF8Encoding(false));
        }

        public static Dictionary<string, object> Obj()
        {
            return new Dictionary<string, object>();
        }

        static void Write(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }

            if (v is string s) { WriteString(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is DateTime dt)
            {
                WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture));
                return;
            }
            if (v is float || v is double || v is decimal)
            {
                sb.Append(Convert.ToDouble(v, CultureInfo.InvariantCulture)
                                 .ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is sbyte || v is byte || v is short || v is ushort
                || v is int || v is uint || v is long || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }

            if (v is IDictionary<string, object> map) { WriteObject(sb, map, depth); return; }
            if (v is IDictionary rawMap) { WriteRawObject(sb, rawMap, depth); return; }
            if (v is IEnumerable list) { WriteArray(sb, list, depth); return; }

            // Enum 등 나머지는 문자열로
            WriteString(sb, v.ToString());
        }

        static void WriteObject(StringBuilder sb, IDictionary<string, object> map, int depth)
        {
            if (map.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            bool first = true;
            foreach (var kv in map)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, depth + 1);
                WriteString(sb, kv.Key);
                sb.Append(':');
                if (depth >= 0) sb.Append(' ');
                Write(sb, kv.Value, depth < 0 ? -1 : depth + 1);
            }
            NewLine(sb, depth);
            sb.Append('}');
        }

        static void WriteRawObject(StringBuilder sb, IDictionary map, int depth)
        {
            var copy = new Dictionary<string, object>();
            foreach (DictionaryEntry e in map)
                copy[Convert.ToString(e.Key, CultureInfo.InvariantCulture)] = e.Value;
            WriteObject(sb, copy, depth);
        }

        static void WriteArray(StringBuilder sb, IEnumerable list, int depth)
        {
            bool any = false;
            sb.Append('[');
            foreach (object item in list)
            {
                if (any) sb.Append(',');
                NewLine(sb, depth + 1);
                any = true;
                Write(sb, item, depth < 0 ? -1 : depth + 1);
            }
            if (any) NewLine(sb, depth);
            sb.Append(']');
        }

        static void NewLine(StringBuilder sb, int depth)
        {
            if (depth < 0) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            // C# 은 \uXXXX 를 토큰화 전에 치환한다 — 문자 리터럴 안에서 쓰면
            // 소스가 깨지므로 백슬래시와 U+2028/2029 는 수치로 다룬다.
            const char Backslash = (char)0x5C;
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append(Backslash).Append('"'); break;
                    case Backslash: sb.Append(Backslash).Append(Backslash); break;
                    case '\b': sb.Append(Backslash).Append('b'); break;
                    case '\f': sb.Append(Backslash).Append('f'); break;
                    case '\n': sb.Append(Backslash).Append('n'); break;
                    case '\r': sb.Append(Backslash).Append('r'); break;
                    case '\t': sb.Append(Backslash).Append('t'); break;
                    default:
                        // 제어문자와 U+2028/2029 는 그대로 두면 JS 파서가 깨진다
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append(Backslash).Append('u').Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
