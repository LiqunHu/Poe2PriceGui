using System.Collections.Generic;
using System.Text;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// 文本块解析辅助：花括号匹配、子块定位、client 块剥离、命名块清空等。
/// 移植自 tiny-poe2smoother/src/patches.rs 的文本处理函数。
///
/// 这些函数处理的是 POE2 的 .ao/.aoc/.ot/.otc 文件内容（类似 C 风格的花括号语法），
/// 需要正确跳过字符串字面量、注释和 JSON 数组（因为块体内可能含 animations = '[...]'）。
/// </summary>
internal static class TextBlockParser
{
    #region ASCII 字符分类

    private static bool IsAsciiAlphabetic(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsAsciiAlphanumeric(char c) => IsAsciiAlphabetic(c) || (c >= '0' && c <= '9');

    private static bool IsAsciiWhiteSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

    #endregion

    /// <summary>
    /// 跳过当前位置的语法元素（字符串、行注释、块注释、数组）。
    /// 返回跳过后下一个需要处理的位置。
    /// 对应 Rust: skip_syntax(text, i)。
    /// </summary>
    public static int SkipSyntax(string text, int i)
    {
        if (i >= text.Length) return text.Length;
        var c = text[i];

        switch (c)
        {
            case '"':
                {
                    // 字符串字面量：跳到匹配的结束引号
                    var j = i + 1;
                    while (j < text.Length)
                    {
                        if (text[j] == '\\' && j + 1 < text.Length)
                        {
                            j += 2;
                            continue;
                        }
                        if (text[j] == '"')
                        {
                            return j + 1;
                        }
                        j++;
                    }
                    return text.Length;
                }
            case '/':
                if (i + 1 < text.Length && text[i + 1] == '/')
                {
                    // 行注释：跳到行尾（含换行符）
                    var nl = text.IndexOf('\n', i + 2);
                    return nl < 0 ? text.Length : nl + 1;
                }
                if (i + 1 < text.Length && text[i + 1] == '*')
                {
                    // 块注释：跳到 */
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    return close < 0 ? text.Length : close + 2;
                }
                return i + 1;
            case '[':
                {
                    // JSON 数组：需要感知嵌套的字符串和注释
                    var depth = 1;
                    var j = i + 1;
                    while (j < text.Length && depth > 0)
                    {
                        if (text[j] == '"'
                            || (text[j] == '/' && j + 1 < text.Length && (text[j + 1] == '/' || text[j + 1] == '*')))
                        {
                            j = SkipSyntax(text, j);
                            continue;
                        }
                        if (text[j] == '[') depth++;
                        else if (text[j] == ']') depth--;
                        j++;
                    }
                    return j;
                }
            default:
                return i + 1;
        }
    }

    /// <summary>
    /// 从开括号位置查找匹配的闭括号位置。
    /// 对应 Rust: find_matching_brace(text, open)。
    /// 需要跳过字符串、数组、注释中的花括号。
    /// </summary>
    public static int? FindMatchingBrace(string text, int open)
    {
        var depth = 1;
        var i = open + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"'
                || c == '['
                || (c == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*')))
            {
                i = SkipSyntax(text, i);
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
            i++;
        }
        return null;
    }

    /// <summary>
    /// 从指定位置查找下一个子块（标识符 + 空格 + {）。
    /// 返回 (名称起始位置, 名称, 开括号位置)。
    /// 对应 Rust: find_next_sub_block(text, from)。
    /// </summary>
    public static (int NameStart, string Name, int Open)? FindNextSubBlock(string text, int from)
    {
        var i = from;
        while (i < text.Length)
        {
            var c = text[i];
            // 跳过字符串、数组、注释
            if (c == '"'
                || c == '['
                || (c == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*')))
            {
                i = SkipSyntax(text, i);
                continue;
            }
            // 识别标识符开头
            if (IsAsciiAlphabetic(c) || c == '_')
            {
                var start = i;
                i++;
                while (i < text.Length && (IsAsciiAlphanumeric(text[i]) || text[i] == '_'))
                {
                    i++;
                }
                var nameEnd = i;
                // 跳过空白
                while (i < text.Length && IsAsciiWhiteSpace(text[i]))
                {
                    i++;
                }
                if (i < text.Length && text[i] == '{')
                {
                    return (start, text.Substring(start, nameEnd - start), i);
                }
                continue;
            }
            i++;
        }
        return null;
    }

    /// <summary>
    /// 在顶层查找指定名称的块，返回其开括号位置。
    /// 对应 Rust: find_top_level_block(text, name)。
    /// </summary>
    public static int? FindTopLevelBlock(string text, string name)
    {
        var pos = 0;
        while (true)
        {
            var next = FindNextSubBlock(text, pos);
            if (!next.HasValue) return null;
            var (nameStart, blockName, open) = next.Value;
            if (blockName == name) return open;
            var close = FindMatchingBrace(text, open);
            if (!close.HasValue) return null;
            pos = close.Value + 1;
        }
    }

    /// <summary>
    /// 剥离 client 块中不在 keep 列表中的子块。
    /// 对应 Rust: strip_client_blocks(data, keep)。
    ///
    /// 保留 client 块本身及其外围文本，只删除其中的非保留子块。
    /// 用于 effects 补丁：保留 ClientAnimationController/SoundEvents/BoneGroups/AnimatedRender/SkinMesh。
    /// </summary>
    public static string StripClientBlocks(string data, HashSet<string> keep)
    {
        var clientOpen = FindTopLevelBlock(data, "client");
        if (!clientOpen.HasValue) return data;

        var clientClose = FindMatchingBrace(data, clientOpen.Value);
        if (!clientClose.HasValue) return data;

        var bodyStart = clientOpen.Value + 1;
        var bodyLen = clientClose.Value - bodyStart;
        var body = data.Substring(bodyStart, bodyLen);

        var result = new StringBuilder(body.Length);
        var pos = 0;
        while (true)
        {
            var next = FindNextSubBlock(body, pos);
            if (!next.HasValue)
            {
                result.Append(body, pos, body.Length - pos);
                break;
            }
            var (nameStart, name, open) = next.Value;
            var close = FindMatchingBrace(body, open);
            if (!close.HasValue)
            {
                result.Append(body, pos, body.Length - pos);
                break;
            }
            // 保留 pos 到 nameStart 之间的文本（子块之间的分隔符等）
            result.Append(body, pos, nameStart - pos);
            if (keep.Contains(name))
            {
                // 保留整个子块（含花括号）
                result.Append(body, nameStart, close.Value + 1 - nameStart);
            }
            pos = close.Value + 1;
        }
        result.Append(body, pos, body.Length - pos);

        return data.Substring(0, clientOpen.Value + 1)
             + result.ToString()
             + data.Substring(clientClose.Value);
    }

    /// <summary>
    /// 将所有名为 names 中之一的块的体清空（Name { ... } → Name {}）。
    /// 对应 Rust: empty_named_blocks(data, names)。
    ///
    /// 保留块名只清空体，用于声音补丁：SoundEvents { ... } → SoundEvents {}。
    /// 需要在任意嵌套深度工作，且只清空有内容的块（已为空的块保持不变）。
    /// </summary>
    public static string EmptyNamedBlocks(string data, HashSet<string> names)
    {
        var outStr = data;
        var pos = 0;
        while (true)
        {
            var next = FindNextSubBlock(outStr, pos);
            if (!next.HasValue) break;
            var (nameStart, name, open) = next.Value;
            var close = FindMatchingBrace(outStr, open);
            if (!close.HasValue) break;

            // 检查块体是否有非空白内容
            var bodyStart = open + 1;
            var bodyLen = close.Value - bodyStart;
            var bodyHasContent = false;
            for (var i = bodyStart; i < close.Value; i++)
            {
                if (!IsAsciiWhiteSpace(outStr[i]))
                {
                    bodyHasContent = true;
                    break;
                }
            }

            if (names.Contains(name) && bodyHasContent)
            {
                var replacement = $"{name} {{}}";
                var advance = nameStart + replacement.Length;
                outStr = outStr.Remove(nameStart, close.Value + 1 - nameStart);
                outStr = outStr.Insert(nameStart, replacement);
                pos = advance;
            }
            else
            {
                pos = open + 1;
            }
        }
        return outStr;
    }

    /// <summary>
    /// 移除所有对指定函数的调用（包括限定名前缀如 controller.CreateCameraPanNode(...)）。
    /// 对应 Rust: remove_function_calls(data, func)。
    ///
    /// 逻辑：找到函数名 → 向左扩展限定名（字母数字下划线点）→
    ///       跳过空白找到 '(' → 匹配括号 → 跳过空白找到 ';' → 删除整段。
    /// </summary>
    public static string RemoveFunctionCalls(string data, string func)
    {
        var pos = 0;
        while (true)
        {
            var found = data.IndexOf(func, pos, StringComparison.Ordinal);
            if (found < 0) break;

            // 向左扩展限定名前缀
            var start = found;
            while (start > 0)
            {
                var ch = data[start - 1];
                if (IsAsciiAlphanumeric(ch) || ch == '_' || ch == '.')
                {
                    start--;
                }
                else
                {
                    break;
                }
            }

            // 跳过函数名后的空白，找开括号
            var paren = found + func.Length;
            while (paren < data.Length && IsAsciiWhiteSpace(data[paren]))
            {
                paren++;
            }
            if (paren >= data.Length || data[paren] != '(')
            {
                pos = found + 1;
                continue;
            }

            // 匹配括号
            var depth = 1;
            var end = paren + 1;
            while (end < data.Length && depth > 0)
            {
                var c = data[end];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                end++;
            }

            // 跳过闭括号后的空白，找分号
            while (end < data.Length && IsAsciiWhiteSpace(data[end]))
            {
                end++;
            }
            if (depth == 0 && end < data.Length && data[end] == ';')
            {
                data = data.Remove(start, end + 1 - start);
                pos = start;
            }
            else
            {
                pos = found + 1;
            }
        }
        return data;
    }

    /// <summary>
    /// 将 JSON 数组属性的值替换为空数组。
    /// 对应 Rust: replace_array_property(data, property)。
    ///
    /// 例如："nodes": [1, 2, 3], → "nodes": [],
    /// 用于 atlas-fog 补丁清空 nodes/links 数组。
    /// </summary>
    public static string ReplaceArrayProperty(string data, string property)
    {
        var pattern = $"\"{property}\":";
        var index = data.IndexOf(pattern, StringComparison.Ordinal);
        if (index < 0) return data;

        var bracketStart = data.IndexOf('[', index);
        if (bracketStart < 0) return data;

        // 匹配方括号
        var depth = 1;
        var end = bracketStart + 1;
        while (end < data.Length && depth > 0)
        {
            var c = data[end];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            end++;
        }

        if (depth != 0) return data;

        // end 现在指向 ']' 的下一个字符；']' 在 end - 1
        // 在 ] 之后找逗号（用于连同替换）
        var commaPos = data.IndexOf(',', end - 1);
        if (commaPos >= 0 && commaPos < end + 5)
        {
            data = data.Remove(index, commaPos - index + 1);
            data = data.Insert(index, $"\"{property}\": [],");
        }
        return data;
    }
}
