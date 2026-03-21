using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// 轻量代码语法高亮器 —— 关键字着色，无外部依赖。
/// 支持 24+ 语言，VS Code Dark+ 配色。
/// 从 ChatMarkdownBlock 中提取为独立模块，可复用于任意 Markdown 渲染场景。
/// </summary>
internal static class CodeHighlighter
{
    // ── 颜色 token（VS Code Dark+ 风格，亮暗主题通用性好） ──
    private static readonly IBrush KeywordBrush   = Brush("#569CD6"); // blue
    private static readonly IBrush StringBrush    = Brush("#CE9178"); // orange
    private static readonly IBrush CommentBrush   = Brush("#6A9955"); // green
    private static readonly IBrush NumberBrush    = Brush("#B5CEA8"); // light green
    private static readonly IBrush TypeBrush      = Brush("#4EC9B0"); // teal
    private static readonly IBrush FuncBrush      = Brush("#DCDCAA"); // yellow
    private static readonly IBrush OperatorBrush  = Brush("#D4D4D4"); // light gray
    private static readonly IBrush PuncBrush      = Brush("#808080"); // gray
    private static readonly IBrush PropKeyBrush   = Brush("#9CDCFE"); // light blue (JSON keys)
    private static readonly IBrush BoolNullBrush  = Brush("#569CD6"); // same as keyword
    private static readonly IBrush TagBrush       = Brush("#569CD6"); // XML tags
    private static readonly IBrush AttrNameBrush  = Brush("#9CDCFE"); // XML attr name
    private static readonly IBrush AttrValueBrush = Brush("#CE9178"); // XML attr value
    private static readonly IBrush DecoratorBrush = Brush("#DCDCAA"); // decorators/attributes

    // ── 语言支持检查 ─────────────────────────────────────

    private static readonly HashSet<string> s_supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "py", "csharp", "cs", "c#",
        "javascript", "js", "typescript", "ts",
        "json", "jsonc",
        "xml", "html", "xaml", "axaml", "svg",
        "bash", "sh", "shell", "powershell", "ps1", "pwsh",
        "sql",
        "css", "scss",
        "yaml", "yml",
        "rust", "rs",
        "go", "golang",
        "java", "kotlin", "kt",
        "cpp", "c++", "c", "h",
    };

    public static bool CanHighlight(string lang) => s_supported.Contains(lang);

    // ── 主入口 ───────────────────────────────────────────

    public static void Highlight(InlineCollection inlines, string code, string lang)
    {
        var tokens = Tokenize(code, lang);
        foreach (var (text, brush) in tokens)
        {
            var run = new Run(text) { FontFamily = MarkdownTheme.MonoFont, FontSize = 13 };
            if (brush != null) run.Foreground = brush;
            inlines.Add(run);
        }
    }

    // ── 分词 ─────────────────────────────────────────────

    private static List<(string Text, IBrush? Brush)> Tokenize(string code, string lang)
    {
        if (IsJsonLang(lang)) return TokenizeJson(code);
        if (IsXmlLang(lang)) return TokenizeXml(code);

        var keywords = GetKeywords(lang);
        var types = GetBuiltinTypes(lang);
        var lineComment = GetLineComment(lang);
        var blockStart = GetBlockCommentStart(lang);
        var blockEnd = GetBlockCommentEnd(lang);
        var hasDecorators = lang is "python" or "py" or "csharp" or "cs" or "c#" or "java" or "kotlin" or "kt" or "typescript" or "ts";

        var result = new List<(string, IBrush?)>();
        int i = 0;

        while (i < code.Length)
        {
            // Block comment
            if (blockStart != null && code.AsSpan(i).StartsWith(blockStart))
            {
                int end = code.IndexOf(blockEnd!, i + blockStart.Length, StringComparison.Ordinal);
                if (end < 0) end = code.Length - blockEnd!.Length;
                end += blockEnd!.Length;
                result.Add((code[i..end], CommentBrush));
                i = end;
                continue;
            }

            // Line comment
            if (lineComment != null && code.AsSpan(i).StartsWith(lineComment))
            {
                int end = code.IndexOf('\n', i);
                if (end < 0) end = code.Length;
                result.Add((code[i..end], CommentBrush));
                i = end;
                continue;
            }

            // String (double or single quote, with escape support)
            if (code[i] is '"' or '\'')
            {
                int end = ScanString(code, i);
                result.Add((code[i..end], StringBrush));
                i = end;
                continue;
            }

            // Backtick template string (JS/TS)
            if (code[i] == '`' && lang is "javascript" or "js" or "typescript" or "ts")
            {
                int end = code.IndexOf('`', i + 1);
                if (end < 0) end = code.Length - 1;
                end++;
                result.Add((code[i..end], StringBrush));
                i = end;
                continue;
            }

            // Number
            if (char.IsDigit(code[i]) || (code[i] == '.' && i + 1 < code.Length && char.IsDigit(code[i + 1])))
            {
                int end = i;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] is '.' or 'x' or 'X' or '_'))
                    end++;
                result.Add((code[i..end], NumberBrush));
                i = end;
                continue;
            }

            // Decorator (@xxx or [xxx])
            if (hasDecorators && code[i] == '@' && i + 1 < code.Length && char.IsLetter(code[i + 1]))
            {
                int end = i + 1;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '.'))
                    end++;
                result.Add((code[i..end], DecoratorBrush));
                i = end;
                continue;
            }

            // Word (identifier / keyword)
            if (char.IsLetter(code[i]) || code[i] == '_')
            {
                int end = i;
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_'))
                    end++;
                var word = code[i..end];

                IBrush? brush = null;
                if (keywords.Contains(word))
                    brush = KeywordBrush;
                else if (types.Contains(word))
                    brush = TypeBrush;
                else if (word is "true" or "false" or "True" or "False" or "null" or "None" or "nil")
                    brush = BoolNullBrush;
                else if (end < code.Length && code[end] == '(')
                    brush = FuncBrush;
                else if (word.Length > 1 && char.IsUpper(word[0]))
                    brush = TypeBrush; // PascalCase → likely type

                result.Add((word, brush));
                i = end;
                continue;
            }

            // Operators & punctuation
            if (code[i] is '=' or '+' or '-' or '*' or '/' or '%' or '!' or '<' or '>' or '&' or '|' or '^' or '~' or '?')
            {
                result.Add((code[i].ToString(), OperatorBrush));
                i++;
                continue;
            }

            if (code[i] is '{' or '}' or '(' or ')' or '[' or ']' or ';' or ',' or ':' or '.')
            {
                result.Add((code[i].ToString(), PuncBrush));
                i++;
                continue;
            }

            // Whitespace / other
            result.Add((code[i].ToString(), null));
            i++;
        }

        return result;
    }

    // ── JSON 分词 ────────────────────────────────────────

    private static List<(string, IBrush?)> TokenizeJson(string code)
    {
        var result = new List<(string, IBrush?)>();
        // Simple line-comment aware JSON (jsonc)
        var regex = new Regex(
            @"(//[^\n]*)|" +                             // line comment
            @"(""(?:[^""\\]|\\.)*"")\s*(:)|" +           // key: "xxx":
            @"(""(?:[^""\\]|\\.)*"")|" +                 // string value
            @"(\b(?:true|false|null)\b)|" +              // bool/null
            @"(-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)|" + // number
            @"([{}\[\]:,])",                             // punctuation
            RegexOptions.Compiled);

        int last = 0;
        foreach (Match m in regex.Matches(code))
        {
            if (m.Index > last) result.Add((code[last..m.Index], null));

            if (m.Groups[1].Success) result.Add((m.Value, CommentBrush));
            else if (m.Groups[2].Success)
            {
                result.Add((m.Groups[2].Value, PropKeyBrush));
                result.Add((":", PuncBrush));
                // skip trailing whitespace between key and ':'
                int colonPos = code.IndexOf(':', m.Groups[2].Index + m.Groups[2].Length);
                last = colonPos + 1;
                continue;
            }
            else if (m.Groups[4].Success) result.Add((m.Value, StringBrush));
            else if (m.Groups[5].Success) result.Add((m.Value, BoolNullBrush));
            else if (m.Groups[6].Success) result.Add((m.Value, NumberBrush));
            else if (m.Groups[7].Success) result.Add((m.Value, PuncBrush));
            else result.Add((m.Value, null));

            last = m.Index + m.Length;
        }
        if (last < code.Length) result.Add((code[last..], null));
        return result;
    }

    // ── XML/HTML 分词 ────────────────────────────────────

    private static List<(string, IBrush?)> TokenizeXml(string code)
    {
        var result = new List<(string, IBrush?)>();
        var regex = new Regex(
            @"(<!--[\s\S]*?-->)|" +                        // comment
            @"(</?)([\w:.-]+)|" +                          // tag open: <tag or </tag
            @"([\w:.-]+)\s*(=)\s*(""[^""]*""|'[^']*')|" + // attr="val"
            @"(/?>)|" +                                    // close bracket
            @"(""[^""]*""|'[^']*')",                       // standalone strings
            RegexOptions.Compiled);

        int last = 0;
        foreach (Match m in regex.Matches(code))
        {
            if (m.Index > last) result.Add((code[last..m.Index], null));

            if (m.Groups[1].Success) result.Add((m.Value, CommentBrush));
            else if (m.Groups[2].Success)
            {
                result.Add((m.Groups[2].Value, PuncBrush));   // < or </
                result.Add((m.Groups[3].Value, TagBrush));     // tag name
            }
            else if (m.Groups[4].Success)
            {
                result.Add((m.Groups[4].Value, AttrNameBrush));
                result.Add((m.Groups[5].Value, PuncBrush));    // =
                result.Add((m.Groups[6].Value, AttrValueBrush));
            }
            else if (m.Groups[7].Success) result.Add((m.Value, PuncBrush));
            else if (m.Groups[8].Success) result.Add((m.Value, StringBrush));
            else result.Add((m.Value, null));

            last = m.Index + m.Length;
        }
        if (last < code.Length) result.Add((code[last..], null));
        return result;
    }

    // ── 字符串扫描 ───────────────────────────────────────

    private static int ScanString(string code, int start)
    {
        char quote = code[start];
        int i = start + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\') { i += 2; continue; }
            if (code[i] == quote) { i++; break; }
            if (code[i] == '\n') break; // single-line strings
            i++;
        }
        return i;
    }

    // ── 语言分类辅助 ────────────────────────────────────

    private static bool IsJsonLang(string lang) => lang is "json" or "jsonc";
    private static bool IsXmlLang(string lang) => lang is "xml" or "html" or "xaml" or "axaml" or "svg";

    private static string? GetLineComment(string lang) => lang switch
    {
        "python" or "py" or "bash" or "sh" or "shell" or "powershell" or "ps1" or "pwsh" or "yaml" or "yml" => "#",
        "sql" => "--",
        _ => "//",
    };

    private static string? GetBlockCommentStart(string lang) => lang switch
    {
        "python" or "py" or "bash" or "sh" or "shell" or "powershell" or "ps1" or "pwsh" or "yaml" or "yml" => null,
        "html" or "xml" or "xaml" or "axaml" or "svg" => "<!--",
        _ => "/*",
    };

    private static string? GetBlockCommentEnd(string lang) => lang switch
    {
        "html" or "xml" or "xaml" or "axaml" or "svg" => "-->",
        "python" or "py" or "bash" or "sh" or "shell" or "powershell" or "ps1" or "pwsh" or "yaml" or "yml" => null,
        _ => "*/",
    };

    private static HashSet<string> GetKeywords(string lang) => lang switch
    {
        "python" or "py" => s_pythonKw,
        "csharp" or "cs" or "c#" => s_csharpKw,
        "javascript" or "js" => s_jsKw,
        "typescript" or "ts" => s_tsKw,
        "bash" or "sh" or "shell" => s_bashKw,
        "powershell" or "ps1" or "pwsh" => s_pwshKw,
        "sql" => s_sqlKw,
        "css" or "scss" => s_cssKw,
        "rust" or "rs" => s_rustKw,
        "go" or "golang" => s_goKw,
        "java" => s_javaKw,
        "kotlin" or "kt" => s_kotlinKw,
        "cpp" or "c++" or "c" or "h" => s_cppKw,
        _ => new(),
    };

    private static HashSet<string> GetBuiltinTypes(string lang) => lang switch
    {
        "csharp" or "cs" or "c#" => s_csharpTypes,
        "typescript" or "ts" => s_tsTypes,
        "java" => s_javaTypes,
        "rust" or "rs" => s_rustTypes,
        "go" or "golang" => s_goTypes,
        "cpp" or "c++" or "c" or "h" => s_cppTypes,
        "python" or "py" => s_pythonTypes,
        _ => new(),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    // ── 关键字集合 ───────────────────────────────────────

    private static readonly HashSet<string> s_pythonKw =
    [ "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield" ];

    private static readonly HashSet<string> s_pythonTypes =
    [ "int", "float", "str", "bool", "list", "dict", "tuple", "set", "bytes", "object", "type", "range", "print", "len", "enumerate", "zip", "map", "filter", "sorted", "reversed", "isinstance", "super", "property", "classmethod", "staticmethod" ];

    private static readonly HashSet<string> s_csharpKw =
    [ "abstract", "as", "async", "await", "base", "break", "case", "catch", "checked", "class", "const", "continue", "default", "delegate", "do", "else", "enum", "event", "explicit", "extern", "finally", "fixed", "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "interface", "internal", "is", "lock", "namespace", "new", "operator", "out", "override", "params", "partial", "private", "protected", "public", "readonly", "record", "ref", "required", "return", "sealed", "set", "sizeof", "static", "struct", "switch", "this", "throw", "try", "typeof", "unchecked", "unsafe", "using", "value", "var", "virtual", "void", "volatile", "when", "where", "while", "yield" ];

    private static readonly HashSet<string> s_csharpTypes =
    [ "bool", "byte", "char", "decimal", "double", "float", "int", "long", "nint", "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort", "dynamic", "Task", "List", "Dictionary", "HashSet", "IEnumerable", "Action", "Func", "Span", "Memory" ];

    private static readonly HashSet<string> s_jsKw =
    [ "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "export", "extends", "finally", "for", "from", "function", "if", "import", "in", "instanceof", "let", "new", "of", "return", "static", "super", "switch", "this", "throw", "try", "typeof", "var", "void", "while", "with", "yield" ];

    private static readonly HashSet<string> s_tsKw =
    [ "abstract", "any", "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "declare", "default", "delete", "do", "else", "enum", "export", "extends", "finally", "for", "from", "function", "get", "if", "implements", "import", "in", "instanceof", "interface", "is", "keyof", "let", "module", "namespace", "new", "of", "override", "readonly", "return", "set", "static", "super", "switch", "this", "throw", "try", "type", "typeof", "var", "void", "while", "with", "yield" ];

    private static readonly HashSet<string> s_tsTypes =
    [ "string", "number", "boolean", "object", "symbol", "bigint", "unknown", "never", "void", "undefined", "null", "Array", "Promise", "Record", "Partial", "Required", "Readonly", "Pick", "Omit", "Map", "Set" ];

    private static readonly HashSet<string> s_bashKw =
    [ "if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "in", "function", "return", "local", "export", "source", "echo", "exit", "set", "unset", "read", "shift", "trap" ];

    private static readonly HashSet<string> s_pwshKw =
    [ "Begin", "Break", "Catch", "Class", "Continue", "Data", "Define", "Do", "DynamicParam", "Else", "ElseIf", "End", "Exit", "Filter", "Finally", "For", "ForEach", "From", "Function", "If", "In", "Param", "Process", "Return", "Switch", "Throw", "Trap", "Try", "Until", "Using", "While", "Workflow" ];

    private static readonly HashSet<string> s_sqlKw =
    [ "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "TABLE", "ALTER", "DROP", "INDEX", "ON", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "FULL", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "ALL", "DISTINCT", "AS", "IS", "NULL", "LIKE", "IN", "BETWEEN", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END", "COUNT", "SUM", "AVG", "MIN", "MAX", "CAST", "COALESCE", "NULLIF",
      "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete", "create", "table", "alter", "drop", "index", "on", "join", "left", "right", "inner", "outer", "cross", "full", "group", "by", "order", "having", "limit", "offset", "union", "all", "distinct", "as", "is", "null", "like", "in", "between", "exists", "case", "when", "then", "else", "end", "count", "sum", "avg", "min", "max", "cast", "coalesce", "nullif" ];

    private static readonly HashSet<string> s_cssKw =
    [ "import", "media", "keyframes", "font-face", "supports", "charset", "namespace", "page" ];

    private static readonly HashSet<string> s_rustKw =
    [ "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super", "trait", "type", "unsafe", "use", "where", "while", "yield" ];

    private static readonly HashSet<string> s_rustTypes =
    [ "i8", "i16", "i32", "i64", "i128", "isize", "u8", "u16", "u32", "u64", "u128", "usize", "f32", "f64", "bool", "char", "str", "String", "Vec", "Box", "Option", "Result", "HashMap", "HashSet", "Arc", "Rc", "Mutex" ];

    private static readonly HashSet<string> s_goKw =
    [ "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto", "if", "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var" ];

    private static readonly HashSet<string> s_goTypes =
    [ "bool", "byte", "complex64", "complex128", "error", "float32", "float64", "int", "int8", "int16", "int32", "int64", "rune", "string", "uint", "uint8", "uint16", "uint32", "uint64", "uintptr" ];

    private static readonly HashSet<string> s_javaKw =
    [ "abstract", "assert", "break", "case", "catch", "class", "const", "continue", "default", "do", "else", "enum", "extends", "final", "finally", "for", "goto", "if", "implements", "import", "instanceof", "interface", "native", "new", "package", "private", "protected", "public", "return", "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try", "void", "volatile", "while", "yield", "sealed", "permits", "record", "var" ];

    private static readonly HashSet<string> s_javaTypes =
    [ "boolean", "byte", "char", "double", "float", "int", "long", "short", "String", "Object", "Integer", "Long", "Double", "Float", "Boolean", "Character", "Byte", "Short", "List", "Map", "Set", "ArrayList", "HashMap", "HashSet", "Optional" ];

    private static readonly HashSet<string> s_kotlinKw =
    [ "abstract", "actual", "annotation", "as", "break", "by", "catch", "class", "companion", "const", "constructor", "continue", "crossinline", "data", "do", "else", "enum", "expect", "external", "final", "finally", "for", "fun", "get", "if", "import", "in", "infix", "init", "inline", "inner", "interface", "internal", "is", "it", "lateinit", "noinline", "object", "open", "operator", "out", "override", "package", "private", "protected", "public", "reified", "return", "sealed", "set", "super", "suspend", "this", "throw", "try", "typealias", "typeof", "val", "var", "vararg", "when", "where", "while" ];

    private static readonly HashSet<string> s_cppKw =
    [ "alignas", "alignof", "asm", "auto", "break", "case", "catch", "class", "const", "constexpr", "continue", "co_await", "co_return", "co_yield", "decltype", "default", "delete", "do", "else", "enum", "explicit", "export", "extern", "for", "friend", "goto", "if", "inline", "mutable", "namespace", "new", "noexcept", "operator", "private", "protected", "public", "register", "return", "sizeof", "static", "static_assert", "static_cast", "struct", "switch", "template", "this", "thread_local", "throw", "try", "typedef", "typeid", "typename", "union", "using", "virtual", "volatile", "while",
      "#include", "#define", "#ifdef", "#ifndef", "#endif", "#pragma", "#if", "#else", "#elif" ];

    private static readonly HashSet<string> s_cppTypes =
    [ "void", "bool", "char", "wchar_t", "char8_t", "char16_t", "char32_t", "short", "int", "long", "float", "double", "signed", "unsigned", "size_t", "ptrdiff_t", "nullptr_t", "string", "vector", "map", "set", "array", "unique_ptr", "shared_ptr", "optional", "variant", "tuple", "pair" ];
}
