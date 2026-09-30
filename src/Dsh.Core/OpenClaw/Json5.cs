using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

/// <summary>A forgiving reader for JSON5 — what OpenClaw writes its config in: comments, unquoted keys,
/// single-quoted strings, trailing commas, hex numbers, +/- signs, Infinity/NaN. It produces the same
/// <see cref="JsonNode"/> tree System.Text.Json would for strict JSON, and never throws on bad input:
/// <see cref="TryParse"/> says why it gave up.</summary>
public static class Json5
{
    public static JsonNode? TryParse(string text, out string? error)
    {
        try
        {
            var parser = new Parser(text);
            var node = parser.ParseDocument();
            error = null;
            return node;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private sealed class Parser(string text)
    {
        private int _i;

        public JsonNode? ParseDocument()
        {
            // A byte-order mark or a shebang-like first line is not part of the value.
            if (_i < text.Length && text[_i] == '﻿') _i++;
            SkipTrivia();
            var value = ParseValue(0);
            SkipTrivia();
            if (_i < text.Length) throw Fail("unexpected text after the value");
            return value;
        }

        private FormatException Fail(string what)
        {
            var line = 1;
            var column = 1;
            for (var k = 0; k < Math.Min(_i, text.Length); k++)
            {
                if (text[k] == '\n') { line++; column = 1; }
                else column++;
            }
            return new FormatException($"{what} (line {line}, column {column})");
        }

        private void SkipTrivia()
        {
            while (_i < text.Length)
            {
                var c = text[_i];
                if (char.IsWhiteSpace(c) || c == '﻿') { _i++; continue; }
                if (c == '/' && _i + 1 < text.Length && text[_i + 1] == '/')
                {
                    while (_i < text.Length && text[_i] != '\n') _i++;
                    continue;
                }
                if (c == '/' && _i + 1 < text.Length && text[_i + 1] == '*')
                {
                    var end = text.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    if (end < 0) throw Fail("unterminated comment");
                    _i = end + 2;
                    continue;
                }
                break;
            }
        }

        private JsonNode? ParseValue(int depth)
        {
            if (depth > 200) throw Fail("nested too deeply");
            SkipTrivia();
            if (_i >= text.Length) throw Fail("unexpected end of text");
            var c = text[_i];
            switch (c)
            {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"' or '\'': return JsonValue.Create(ParseString());
            }
            if (c is '-' or '+' or '.' || char.IsDigit(c)) return ParseNumber();
            var word = ParseIdentifier();
            return word switch
            {
                "true" => JsonValue.Create(true),
                "false" => JsonValue.Create(false),
                "null" => null,
                "Infinity" or "NaN" => JsonValue.Create(0),
                // (The word itself isn't quoted back: in a settings file that is likely to be a key someone forgot the quotes on.)
                _ => throw Fail("unexpected word where a value belongs"),
            };
        }

        private JsonObject ParseObject(int depth)
        {
            var result = new JsonObject();
            _i++; // {
            while (true)
            {
                SkipTrivia();
                if (_i >= text.Length) throw Fail("unterminated object");
                if (text[_i] == '}') { _i++; return result; }
                var key = text[_i] is '"' or '\'' ? ParseString() : ParseIdentifier();
                SkipTrivia();
                if (_i >= text.Length || text[_i] != ':') throw Fail($"expected ':' after \"{key}\"");
                _i++;
                result[key] = ParseValue(depth + 1);
                SkipTrivia();
                if (_i >= text.Length) throw Fail("unterminated object");
                if (text[_i] == ',') { _i++; continue; }
                if (text[_i] == '}') { _i++; return result; }
                throw Fail("expected ',' or '}'");
            }
        }

        private JsonArray ParseArray(int depth)
        {
            var result = new JsonArray();
            _i++; // [
            while (true)
            {
                SkipTrivia();
                if (_i >= text.Length) throw Fail("unterminated array");
                if (text[_i] == ']') { _i++; return result; }
                result.Add(ParseValue(depth + 1));
                SkipTrivia();
                if (_i >= text.Length) throw Fail("unterminated array");
                if (text[_i] == ',') { _i++; continue; }
                if (text[_i] == ']') { _i++; return result; }
                throw Fail("expected ',' or ']'");
            }
        }

        private string ParseIdentifier()
        {
            var start = _i;
            while (_i < text.Length && (char.IsLetterOrDigit(text[_i]) || text[_i] is '_' or '$' or '-' or '.' or '@')) _i++;
            if (_i == start) throw Fail("expected a name or value");
            return text[start.._i];
        }

        private string ParseString()
        {
            var quote = text[_i++];
            var value = new StringBuilder();
            while (true)
            {
                if (_i >= text.Length) throw Fail("unterminated string");
                var c = text[_i++];
                if (c == quote) return value.ToString();
                if (c != '\\') { value.Append(c); continue; }
                if (_i >= text.Length) throw Fail("unterminated string");
                var e = text[_i++];
                switch (e)
                {
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'v': value.Append('\v'); break;
                    case '0': value.Append('\0'); break;
                    case '\r': if (_i < text.Length && text[_i] == '\n') _i++; break; // line continuation
                    case '\n': break;
                    case 'x':
                        value.Append((char)ReadHex(2));
                        break;
                    case 'u':
                        value.Append((char)ReadHex(4));
                        break;
                    default: value.Append(e); break;
                }
            }
        }

        private int ReadHex(int digits)
        {
            if (_i + digits > text.Length || !int.TryParse(text.AsSpan(_i, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n))
                throw Fail("bad escape sequence");
            _i += digits;
            return n;
        }

        private JsonNode? ParseNumber()
        {
            var start = _i;
            if (text[_i] is '-' or '+') _i++;
            if (_i < text.Length && (text[_i] is 'I' or 'N'))
            {
                ParseIdentifier();
                return JsonValue.Create(0);
            }
            if (_i + 1 < text.Length && text[_i] == '0' && text[_i + 1] is 'x' or 'X')
            {
                _i += 2;
                var hexStart = _i;
                while (_i < text.Length && Uri.IsHexDigit(text[_i])) _i++;
                if (_i == hexStart) throw Fail("bad hex number");
                var negative = text[start] == '-';
                if (!long.TryParse(text.AsSpan(hexStart, _i - hexStart), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var magnitude) || magnitude < 0)
                    throw Fail("hex number too large");
                var signed = negative ? -magnitude : magnitude;
                return signed is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)signed) : JsonValue.Create(signed);
            }
            while (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] is '.' or 'e' or 'E' or '-' or '+')) _i++;
            var raw = text[start.._i].TrimStart('+');
            if (raw.StartsWith('.')) raw = "0" + raw;
            if (raw.StartsWith("-.", StringComparison.Ordinal)) raw = "-0" + raw[1..];
            if (raw.EndsWith('.')) raw += "0";
            if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                return whole is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)whole) : JsonValue.Create(whole);
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)) return JsonValue.Create(real);
            throw Fail($"bad number '{raw}'");
        }
    }
}
