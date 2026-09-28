namespace Dsh.Core;

/// <summary>A language's lexical vocabulary. Deliberately shallow: enough to colour comments,
/// strings, numbers, and keywords, which is what makes code readable at a glance. It is not a parser
/// and does not try to be.</summary>
public sealed record Language(
    string Name,
    IReadOnlySet<string> Keywords,
    string? LineComment,
    (string Open, string Close)? BlockComment,
    /// <summary>Quote characters that open a string literal.</summary>
    IReadOnlySet<char> Quotes,
    /// <summary>Strings may contain backslash escapes.</summary>
    bool Escapes,
    /// <summary>Keywords match regardless of case (PowerShell, SQL, batch).</summary>
    bool IgnoreCase = false)
{
    public static Language Detect(string path)
    {
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var name = System.IO.Path.GetFileName(path).ToLowerInvariant();
        if (name is "makefile" or "dockerfile" || ext == "mk") return Shell;
        return ext switch
        {
            "swift" => Swift,
            "cs" or "csx" => CSharp,
            "c" or "h" or "cc" or "cpp" or "hpp" or "m" or "mm" or "java" or "kt" or "go" or "rs" or
                "js" or "jsx" or "ts" or "tsx" or "mjs" or "cjs" or "dart" or "scala" or "php" or "zig" => CFamily(ext),
            "py" or "rb" or "pl" or "r" or "jl" => Scripting(ext),
            "ps1" or "psm1" or "psd1" => PowerShell,
            "bat" or "cmd" => Batch,
            "sh" or "bash" or "zsh" or "fish" or "env" or "gitignore" or "conf" or "cfg" or "ini" or
                "properties" or "toml" or "gradle" or "podspec" or "xcconfig" or "editorconfig" or "gitattributes" => Shell,
            "json" => Json,
            "yaml" or "yml" => Yaml,
            "html" or "htm" or "xml" or "svg" or "plist" or "entitlements" or "xaml" or "csproj" or "vbproj" or
                "fsproj" or "props" or "targets" or "resx" or "config" or "nuspec" or "manifest" or "wxs" => Markup,
            "css" or "scss" or "sass" or "less" => Css,
            "sql" => Sql,
            _ => Plain,
        };
    }

    public static Language Plain { get; } = new("Text", new HashSet<string>(), null, null, new HashSet<char>(), false);

    public static Language Swift { get; } = new("Swift",
        new HashSet<string>
        {
            "associatedtype", "class", "deinit", "enum", "extension", "fileprivate", "func", "import", "init", "inout",
            "internal", "let", "open", "operator", "private", "protocol", "public", "rethrows", "static", "struct",
            "subscript", "typealias", "var", "actor", "async", "await", "break", "case", "continue", "default", "defer",
            "do", "else", "fallthrough", "for", "guard", "if", "in", "repeat", "return", "switch", "where", "while",
            "as", "catch", "false", "is", "nil", "super", "self", "Self", "throw", "throws", "true", "try", "some",
            "any", "final", "lazy", "weak", "unowned", "mutating", "nonmutating", "override", "required",
            "convenience", "indirect", "@MainActor", "@Observable", "@State",
        },
        "//", ("/*", "*/"), new HashSet<char> { '"' }, true);

    private static readonly string[] CBase =
    [
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern",
        "float", "for", "goto", "if", "int", "long", "register", "return", "short", "signed", "sizeof", "static",
        "struct", "switch", "typedef", "union", "unsigned", "void", "volatile", "while", "class", "public", "private",
        "protected", "new", "delete", "this", "true", "false", "null", "nullptr", "namespace", "template", "typename",
        "using", "virtual", "override", "final", "try", "catch", "throw",
    ];

    public static Language CSharp { get; } = new("C#",
        new HashSet<string>(CBase)
        {
            "abstract", "as", "async", "await", "base", "bool", "byte", "checked", "decimal", "delegate", "event",
            "explicit", "finally", "fixed", "foreach", "get", "implicit", "in", "init", "interface", "internal", "is",
            "lock", "nameof", "object", "operator", "out", "params", "partial", "readonly", "record", "ref", "required",
            "sbyte", "sealed", "set", "stackalloc", "string", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "value", "var", "when", "where", "with", "yield", "dynamic", "global", "not", "and", "or", "file", "scoped",
        },
        "//", ("/*", "*/"), new HashSet<char> { '"', '\'' }, true);

    public static Language CFamily(string ext)
    {
        var keywords = new HashSet<string>(CBase);
        switch (ext)
        {
            case "js" or "jsx" or "ts" or "tsx" or "mjs" or "cjs":
                keywords.UnionWith(["function", "let", "var", "const", "async", "await", "export", "import", "from", "of",
                    "in", "typeof", "instanceof", "yield", "interface", "type", "implements", "extends", "readonly", "as",
                    "undefined", "NaN"]);
                break;
            case "go":
                keywords.UnionWith(["func", "package", "import", "defer", "go", "chan", "map", "range", "select", "type",
                    "var", "nil", "make", "len", "cap"]);
                break;
            case "rs":
                keywords.UnionWith(["fn", "let", "mut", "impl", "trait", "pub", "crate", "mod", "use", "match", "loop",
                    "move", "ref", "Some", "None", "Ok", "Err", "self", "Self", "where", "dyn", "async", "await"]);
                break;
            case "kt":
                keywords.UnionWith(["fun", "val", "var", "object", "companion", "data", "when", "is", "as", "suspend",
                    "lateinit", "init"]);
                break;
            case "php":
                keywords.UnionWith(["echo", "function", "elseif", "foreach", "endforeach", "array"]);
                break;
        }
        return new Language(ext.ToUpperInvariant(), keywords, "//", ("/*", "*/"), new HashSet<char> { '"', '\'', '`' }, true);
    }

    public static Language Scripting(string ext)
    {
        var keywords = new HashSet<string>
        {
            "and", "as", "assert", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for",
            "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return",
            "try", "while", "with", "yield", "True", "False", "None", "async", "await", "self",
        };
        if (ext == "rb")
            keywords.UnionWith(["do", "end", "module", "require", "attr_accessor", "nil", "true", "false", "unless", "elsif", "then", "puts"]);
        return new Language(ext.ToUpperInvariant(), keywords, "#", null, new HashSet<char> { '"', '\'' }, true);
    }

    public static Language PowerShell { get; } = new("PowerShell",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam", "else", "elseif", "end", "enum",
            "exit", "filter", "finally", "for", "foreach", "from", "function", "if", "in", "param", "process", "return",
            "switch", "throw", "trap", "try", "until", "using", "while", "workflow", "$true", "$false", "$null",
            "-eq", "-ne", "-gt", "-ge", "-lt", "-le", "-like", "-notlike", "-match", "-notmatch", "-and", "-or", "-not",
        },
        "#", ("<#", "#>"), new HashSet<char> { '"', '\'' }, false, IgnoreCase: true);

    public static Language Batch { get; } = new("Batch",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "echo", "set", "if", "else", "goto", "call", "exit", "for", "in", "do", "not", "exist", "defined",
            "errorlevel", "setlocal", "endlocal", "shift", "pushd", "popd", "rem", "equ", "neq", "lss", "leq", "gtr", "geq",
        },
        "::", null, new HashSet<char> { '"' }, false, IgnoreCase: true);

    public static Language Shell { get; } = new("Shell",
        new HashSet<string>
        {
            "if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "function", "return", "exit",
            "export", "local", "readonly", "source", "echo", "set", "unset", "shift", "trap", "in", "select", "until",
        },
        "#", null, new HashSet<char> { '"', '\'' }, true);

    public static Language Json { get; } = new("JSON", new HashSet<string> { "true", "false", "null" },
        null, null, new HashSet<char> { '"' }, true);

    public static Language Yaml { get; } = new("YAML", new HashSet<string> { "true", "false", "null", "yes", "no", "on", "off" },
        "#", null, new HashSet<char> { '"', '\'' }, true);

    public static Language Markup { get; } = new("Markup", new HashSet<string>(),
        null, ("<!--", "-->"), new HashSet<char> { '"', '\'' }, false);

    public static Language Css { get; } = new("CSS",
        new HashSet<string> { "import", "media", "keyframes", "supports", "font-face", "important", "root" },
        null, ("/*", "*/"), new HashSet<char> { '"', '\'' }, true);

    public static Language Sql { get; } = new("SQL",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "select", "from", "where", "insert", "into", "values", "update", "set", "delete", "create", "table", "drop",
            "alter", "join", "left", "right", "inner", "outer", "on", "group", "by", "order", "having", "limit", "offset",
            "and", "or", "not", "null", "as", "distinct", "union", "index", "primary", "key",
        },
        "--", ("/*", "*/"), new HashSet<char> { '\'', '"' }, true, IgnoreCase: true);

    /// <summary>Whether there is anything to colour at all.</summary>
    public bool IsPlain => Keywords.Count == 0 && LineComment is null && BlockComment is null && Quotes.Count == 0;
}
