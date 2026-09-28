using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

// MARK: - Plugin manifests
//
// A plugin is a JSON manifest that declares extra tools, each backed by a shell command template.
// It needs no compilation and no process protocol — dropping a file in the plugins folder is the
// whole install step.
//
//   {
//     "name": "dotnet",
//     "description": ".NET helpers",
//     "tools": [
//       { "name": "dotnet_test",
//         "description": "Run the test suite. Use after changing code.",
//         "parameters": {"type":"object","properties":{"filter":{"type":"string"}}},
//         "command": "dotnet test --filter ${filter}",
//         "requiresApproval": true }
//     ]
//   }
//
// ${key} in command is replaced by the argument, quoted for the shell the command runs in, so a
// value containing spaces or quotes cannot break out of its position.

public sealed record PluginManifest(string Name, string? Description, string? Version, IReadOnlyList<PluginToolSpec> Tools)
{
    public string Id => Name;
}

public sealed record PluginToolSpec(
    string Name,
    string Description,
    /// <summary>JSON Schema object for the arguments, as text. Null = no arguments.</summary>
    string? Parameters,
    /// <summary>Shell command template. ${key} interpolates an argument.</summary>
    string Command,
    /// <summary>Ask the user before running. Defaults to true — a plugin runs arbitrary shell, and
    /// silence is the wrong default.</summary>
    bool? RequiresApproval = null,
    int? Timeout = null);

// MARK: - The executor

/// <summary>One tool contributed by a plugin. Its identity is a value, not a type.</summary>
public sealed class PluginTool(string plugin, PluginToolSpec declaration) : IToolExecutor
{
    public string Plugin { get; } = plugin;
    public PluginToolSpec Declaration { get; } = declaration;

    public string Name => Declaration.Name;

    public ToolSpec Spec => new(Declaration.Name, Declaration.Description,
        Declaration.Parameters ?? """{"type":"object","properties":{}}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var command = Interpolate(Declaration.Command, JsonArgs.Object(arguments), context.Shell.Kind);
        if (Declaration.RequiresApproval ?? true)
        {
            var approved = await context.RequestPermission($"plugin-{Guid.NewGuid():N}", Declaration.Name,
                $"Plugin {Plugin} wants to run: {command}").ConfigureAwait(false);
            if (!approved) return "Error: the user declined to run this plugin command.";
        }
        var timeout = Math.Clamp(Declaration.Timeout ?? 120, 5, 600);
        var shellArgs = new JsonObject { ["command"] = command, ["timeout"] = timeout }.ToJsonString();
        // Reuse the shell tool so timeout, truncation, and cwd behave identically.
        return await new RunShellCommandTool(context.Shell).ExecuteAsync(shellArgs, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replace ${key} with the quoted argument value. Unknown keys collapse to an empty
    /// (quoted) string so an optional parameter can be omitted.</summary>
    public static string Interpolate(string template, JsonObject args, ShellKind shell = ShellKind.Bash)
    {
        var output = new StringBuilder();
        var rest = template.AsSpan();
        while (true)
        {
            var open = rest.IndexOf("${", StringComparison.Ordinal);
            if (open < 0) break;
            output.Append(rest[..open]);
            var afterOpen = rest[(open + 2)..];
            var close = afterOpen.IndexOf('}');
            if (close < 0)
            {
                output.Append(rest[open..]);
                return output.ToString();
            }
            var key = afterOpen[..close].ToString();
            output.Append(ShellQuoting.Quote(Stringify(args.TryGetPropertyValue(key, out var node) ? node : null), shell));
            rest = afterOpen[(close + 1)..];
        }
        output.Append(rest);
        return output.ToString();
    }

    private static string Stringify(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return "";
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return v.GetValue<string>();
            case JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                return v.GetValue<bool>() ? "true" : "false";
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                return v.TryGetValue<long>(out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : v.GetValue<double>().ToString("R", CultureInfo.InvariantCulture);
            case JsonValue v when v.GetValueKind() == JsonValueKind.Null:
                return "";
            default:
                return value.ToJsonString();
        }
    }
}

/// <summary>Quoting an argument so every character stays literal in the target shell.</summary>
public static class ShellQuoting
{
    public static string Quote(string value, ShellKind shell) => shell switch
    {
        ShellKind.PowerShell => PowerShell(value),
        ShellKind.Cmd => Cmd(value),
        _ => Posix(value),
    };

    /// <summary>Single-quote for bash: everything is literal inside, and an embedded quote is closed,
    /// escaped, and reopened.</summary>
    public static string Posix(string value)
    {
        if (value.Length == 0) return "''";
        if (value.All(c => char.IsLetterOrDigit(c) || "-_./=:@,".Contains(c))) return value;
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    // PowerShell also accepts the typographic single quotes as string delimiters, so they are
    // doubled too — otherwise ‘ could end the string early.
    private static readonly char[] PowerShellQuotes = ['\'', '‘', '’', '‚', '‛'];

    /// <summary>Single-quote for PowerShell: nothing expands inside '…' and a quote is doubled.
    /// "@" and "," stay quoted (splatting / array syntax).</summary>
    public static string PowerShell(string value)
    {
        if (value.Length == 0) return "''";
        if (value.All(c => char.IsLetterOrDigit(c) || "-_./\\:=".Contains(c)) && value[0] != '-') return value;
        var sb = new StringBuilder("'");
        foreach (var c in value)
        {
            sb.Append(c);
            if (Array.IndexOf(PowerShellQuotes, c) >= 0) sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>Best effort for cmd.exe: double quotes (so &amp; | &lt; &gt; ^ are literal), doubled
    /// inner quotes, and % closed out of the quotes and caret-escaped so %NAME% can't expand.</summary>
    public static string Cmd(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.All(c => char.IsLetterOrDigit(c) || "-_./\\:".Contains(c))) return value;
        var escaped = value.Replace("\"", "\"\"");
        return "\"" + string.Join("\"^%\"", escaped.Split('%')) + "\"";
    }
}

// MARK: - Loading

public static class PluginLoader
{
    /// <summary>Plugins ship per-project and per-user; the project's win on a name clash.</summary>
    public static IReadOnlyList<string> Directories(string? project)
    {
        var dirs = new List<string>();
        if (project is not null) dirs.Add(Path.Combine(project, ".dsh", "plugins"));
        dirs.Add(UserDirectory);
        return dirs;
    }

    public static string UserDirectory => AppPaths.Plugins;

    /// <summary>Read every *.json manifest from the plugin directories. Malformed files are reported
    /// rather than crashing the load.</summary>
    public static (IReadOnlyList<PluginManifest> Plugins, IReadOnlyList<string> Errors) Load(string? project)
    {
        var plugins = new List<PluginManifest>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directories(project))
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                try
                {
                    var manifest = Parse(File.ReadAllText(file));
                    if (!seen.Add(manifest.Name)) continue;
                    plugins.Add(manifest);
                }
                catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        return (plugins, errors);
    }

    /// <summary>Decode a manifest. "parameters" may be an inline JSON Schema object or a JSON string.</summary>
    public static PluginManifest Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   as JsonObject ?? throw new FormatException("the manifest must be a JSON object.");
        var name = JsonArgs.String(root, "name") ?? throw new FormatException("missing \"name\".");
        if (root["tools"] is not JsonArray toolsNode) throw new FormatException("missing \"tools\" array.");
        var tools = new List<PluginToolSpec>();
        foreach (var node in toolsNode)
        {
            if (node is not JsonObject t) throw new FormatException("each tool must be an object.");
            var toolName = JsonArgs.String(t, "name") ?? throw new FormatException("a tool is missing \"name\".");
            var description = JsonArgs.String(t, "description") ?? throw new FormatException($"tool {toolName} is missing \"description\".");
            var command = JsonArgs.String(t, "command") ?? throw new FormatException($"tool {toolName} is missing \"command\".");
            string? parameters = t["parameters"] switch
            {
                null => null,
                JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
                JsonValue v when v.GetValueKind() == JsonValueKind.Null => null,
                var other => other.ToJsonString(),
            };
            bool? requiresApproval = t["requiresApproval"] is JsonValue ra && ra.TryGetValue<bool>(out var b) ? b : null;
            int? timeout = JsonNumbers.TryGetInt(t["timeout"], out var seconds) ? seconds : null;
            tools.Add(new PluginToolSpec(toolName, description, parameters, command, requiresApproval, timeout));
        }
        return new PluginManifest(name, JsonArgs.String(root, "description"), JsonArgs.String(root, "version"), tools);
    }

    /// <summary>Flatten manifests into executors, dropping any tool whose name would shadow a built-in.</summary>
    public static IReadOnlyList<IToolExecutor> Tools(IEnumerable<PluginManifest> plugins, IEnumerable<string> reserved)
    {
        var output = new List<IToolExecutor>();
        var taken = new HashSet<string>(reserved, StringComparer.Ordinal);
        foreach (var plugin in plugins)
        {
            foreach (var declaration in plugin.Tools)
            {
                if (!taken.Add(declaration.Name)) continue;
                output.Add(new PluginTool(plugin.Name, declaration));
            }
        }
        return output;
    }

    /// <summary>Write a starter manifest so the folder is never empty on first open.</summary>
    public static string InstallExample()
    {
        var dir = AppPaths.Ensure(UserDirectory);
        var path = Path.Combine(dir, "example.json");
        if (File.Exists(path)) return path;
        File.WriteAllText(path, """
        {
          "name": "example",
          "description": "Sample plugin — edit or delete me.",
          "version": "1",
          "tools": [
            {
              "name": "git_status",
              "description": "Show the working tree status of the project's git repository.",
              "parameters": {"type": "object", "properties": {}},
              "command": "git status --short --branch",
              "requiresApproval": false
            }
          ]
        }
        """, TextUtil.Utf8NoBom);
        return path;
    }
}
