using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CardboardXR.Stereo
{
    // Lightweight, zero-dependency recursive-descent JSON parser for dynamic dictionary-keyed
    // Poly Haven API payloads (/assets?type=hdris and /files/{id}).
    public static class PolyHavenJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                var index = 0;
                return ParseValue(json, ref index);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Dictionary<string, object> GetDict(object obj, string key)
        {
            if (obj is Dictionary<string, object> dict && dict.TryGetValue(key, out var value))
                return value as Dictionary<string, object>;
            return null;
        }

        public static List<object> GetList(object obj, string key)
        {
            if (obj is Dictionary<string, object> dict && dict.TryGetValue(key, out var value))
                return value as List<object>;
            return null;
        }

        public static string GetString(object obj, string key, string fallback = "")
        {
            if (obj is Dictionary<string, object> dict && dict.TryGetValue(key, out var value) && value != null)
            {
                if (value is string s)
                    return s;
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
            }
            return fallback;
        }

        public static long GetLong(object obj, string key, long fallback = 0)
        {
            if (obj is Dictionary<string, object> dict && dict.TryGetValue(key, out var value) && value != null)
            {
                if (value is double d)
                    return (long)d;
                if (value is long l)
                    return l;
                if (value is int i)
                    return i;
                if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return fallback;
        }

        public static double GetDouble(object obj, string key, double fallback = 0.0)
        {
            if (obj is Dictionary<string, object> dict && dict.TryGetValue(key, out var value) && value != null)
            {
                if (value is double d)
                    return d;
                if (value is long l)
                    return l;
                if (value is int i)
                    return i;
                if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return fallback;
        }

        public static string[] GetStringList(object obj, string key)
        {
            var list = GetList(obj, key);
            if (list == null || list.Count == 0)
                return Array.Empty<string>();
            var result = new List<string>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is string s && !string.IsNullOrWhiteSpace(s))
                    result.Add(s);
            }
            return result.ToArray();
        }

        static object ParseValue(string s, ref int index)
        {
            SkipWhitespace(s, ref index);
            if (index >= s.Length)
                return null;

            var c = s[index];
            switch (c)
            {
                case '{':
                    return ParseObject(s, ref index);
                case '[':
                    return ParseArray(s, ref index);
                case '"':
                    return ParseString(s, ref index);
                case 't':
                    if (MatchLiteral(s, ref index, "true"))
                        return true;
                    return null;
                case 'f':
                    if (MatchLiteral(s, ref index, "false"))
                        return false;
                    return null;
                case 'n':
                    if (MatchLiteral(s, ref index, "null"))
                        return null;
                    return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ParseNumber(s, ref index);
                    return null;
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int index)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            index++; // '{'
            while (true)
            {
                SkipWhitespace(s, ref index);
                if (index >= s.Length)
                    return null;
                if (s[index] == '}')
                {
                    index++;
                    return dict;
                }
                if (s[index] != '"')
                    return null;

                var key = ParseString(s, ref index);
                if (key == null)
                    return null;

                SkipWhitespace(s, ref index);
                if (index >= s.Length || s[index] != ':')
                    return null;
                index++; // ':'

                var val = ParseValue(s, ref index);
                dict[key] = val;

                SkipWhitespace(s, ref index);
                if (index >= s.Length)
                    return null;
                if (s[index] == ',')
                {
                    index++;
                    continue;
                }
                if (s[index] == '}')
                {
                    index++;
                    return dict;
                }
                return null;
            }
        }

        static List<object> ParseArray(string s, ref int index)
        {
            var list = new List<object>();
            index++; // '['
            while (true)
            {
                SkipWhitespace(s, ref index);
                if (index >= s.Length)
                    return null;
                if (s[index] == ']')
                {
                    index++;
                    return list;
                }

                var val = ParseValue(s, ref index);
                list.Add(val);

                SkipWhitespace(s, ref index);
                if (index >= s.Length)
                    return null;
                if (s[index] == ',')
                {
                    index++;
                    continue;
                }
                if (s[index] == ']')
                {
                    index++;
                    return list;
                }
                return null;
            }
        }

        static string ParseString(string s, ref int index)
        {
            if (index >= s.Length || s[index] != '"')
                return null;
            index++; // '"'
            var sb = new StringBuilder();
            while (index < s.Length)
            {
                var c = s[index++];
                if (c == '"')
                    return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (index >= s.Length)
                    return null;
                var esc = s[index++];
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
                        if (index + 4 > s.Length)
                            return null;
                        if (!int.TryParse(s.Substring(index, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var code))
                            return null;
                        index += 4;
                        sb.Append((char)code);
                        break;
                    default:
                        sb.Append(esc);
                        break;
                }
            }
            return null;
        }

        static object ParseNumber(string s, ref int index)
        {
            var start = index;
            if (s[index] == '-')
                index++;
            var digits = 0;
            while (index < s.Length && s[index] >= '0' && s[index] <= '9')
            {
                index++;
                digits++;
            }
            if (digits == 0)
                return null;

            if (index < s.Length && s[index] == '.')
            {
                index++;
                var fracDigits = 0;
                while (index < s.Length && s[index] >= '0' && s[index] <= '9')
                {
                    index++;
                    fracDigits++;
                }
                if (fracDigits == 0)
                    return null;
            }

            if (index < s.Length && (s[index] == 'e' || s[index] == 'E'))
            {
                index++;
                if (index < s.Length && (s[index] == '+' || s[index] == '-'))
                    index++;
                var expDigits = 0;
                while (index < s.Length && s[index] >= '0' && s[index] <= '9')
                {
                    index++;
                    expDigits++;
                }
                if (expDigits == 0)
                    return null;
            }

            var token = s.Substring(start, index - start);
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
            return null;
        }

        static bool MatchLiteral(string s, ref int index, string literal)
        {
            if (index + literal.Length > s.Length)
                return false;
            for (var i = 0; i < literal.Length; i++)
            {
                if (s[index + i] != literal[i])
                    return false;
            }
            index += literal.Length;
            return true;
        }

        static void SkipWhitespace(string s, ref int index)
        {
            while (index < s.Length)
            {
                var c = s[index];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    index++;
                else
                    break;
            }
        }
    }
}
