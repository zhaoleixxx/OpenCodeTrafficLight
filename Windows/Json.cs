using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenCodeTrafficLight
{
    /// <summary>
    /// 极简 JSON 解析器。
    /// 只依赖 BCL（net48 没有 System.Text.Json），返回：
    ///   Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / double / bool / null
    /// 这样单文件 exe 不需要任何第三方 DLL。
    /// </summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var parser = new Parser(text);
            try
            {
                object value = parser.ParseValue();
                parser.SkipWhitespace();
                return value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            return value as List<object>;
        }

        public static object Get(object obj, string key)
        {
            var dict = obj as Dictionary<string, object>;
            if (dict == null) return null;
            object value;
            return dict.TryGetValue(key, out value) ? value : null;
        }

        public static string GetString(object obj, string key)
        {
            var value = Get(obj, key);
            return value as string;
        }

        public static double? GetNumber(object obj, string key)
        {
            var value = Get(obj, key);
            if (value is double) return (double)value;
            if (value is string)
            {
                double parsed;
                if (double.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return null;
        }

        public static bool GetBool(object obj, string key, bool fallback = false)
        {
            var value = Get(obj, key);
            if (value is bool) return (bool)value;
            return fallback;
        }

        // ---------------------------------------------------------------- writer

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }

            var s = value as string;
            if (s != null) { WriteString(sb, s); return; }

            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }

            if (value is int || value is long || value is double || value is float || value is decimal)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var pair in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, pair.Key);
                    sb.Append(':');
                    Write(sb, pair.Value);
                }
                sb.Append('}');
                return;
            }

            var list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, value.ToString());
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // --------------------------------------------------------------- parser

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;

            public Parser(string text) { _text = text; _pos = 0; }

            public void SkipWhitespace()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (_pos >= _text.Length) throw new FormatException("unexpected end");

                char c = _text[_pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>();
                _pos++; // {
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '}') { _pos++; return result; }

                while (true)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    if (_pos >= _text.Length || _text[_pos] != ':') throw new FormatException("expected :");
                    _pos++;
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (_pos >= _text.Length) throw new FormatException("unexpected end in object");
                    if (_text[_pos] == ',') { _pos++; continue; }
                    if (_text[_pos] == '}') { _pos++; break; }
                    throw new FormatException("expected , or }");
                }
                return result;
            }

            private List<object> ParseArray()
            {
                var result = new List<object>();
                _pos++; // [
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == ']') { _pos++; return result; }

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (_pos >= _text.Length) throw new FormatException("unexpected end in array");
                    if (_text[_pos] == ',') { _pos++; continue; }
                    if (_text[_pos] == ']') { _pos++; break; }
                    throw new FormatException("expected , or ]");
                }
                return result;
            }

            private string ParseString()
            {
                if (_pos >= _text.Length || _text[_pos] != '"') throw new FormatException("expected string");
                _pos++;
                var sb = new StringBuilder();

                while (true)
                {
                    if (_pos >= _text.Length) throw new FormatException("unterminated string");
                    char c = _text[_pos++];

                    if (c == '"') break;

                    if (c != '\\') { sb.Append(c); continue; }

                    if (_pos >= _text.Length) throw new FormatException("unterminated escape");
                    char esc = _text[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length) throw new FormatException("bad unicode escape");
                            sb.Append((char)Convert.ToInt32(_text.Substring(_pos, 4), 16));
                            _pos += 4;
                            break;
                        default: throw new FormatException("bad escape");
                    }
                }
                return sb.ToString();
            }

            private double ParseNumber()
            {
                int start = _pos;
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _pos++;
                    else break;
                }
                if (start == _pos) throw new FormatException("unexpected character");
                return double.Parse(_text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void Expect(string literal)
            {
                if (_pos + literal.Length > _text.Length ||
                    string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
                    throw new FormatException("unexpected token");
                _pos += literal.Length;
            }
        }
    }
}
