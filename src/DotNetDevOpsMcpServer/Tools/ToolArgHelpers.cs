using System.Text.Json;

namespace DotNetDevOpsMcpServer.Tools;

public static class ToolArgHelpers
{
    public static string? GetString(this Dictionary<string, JsonElement>? args, string key, string? defaultValue = null)
    {
        if (args == null || !args.TryGetValue(key, out var val))
            return defaultValue;

        return val.ValueKind switch
        {
            JsonValueKind.String => val.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
            _ => val.ToString()
        };
    }

    public static bool GetBool(this Dictionary<string, JsonElement>? args, string key, bool defaultValue = false)
    {
        if (args == null || !args.TryGetValue(key, out var val))
            return defaultValue;

        return val.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(val.GetString(), out var b) ? b : defaultValue,
            _ => defaultValue
        };
    }

    public static int? GetInt(this Dictionary<string, JsonElement>? args, string key, int? defaultValue = null)
    {
        if (args == null || !args.TryGetValue(key, out var val))
            return defaultValue;

        return val.ValueKind switch
        {
            JsonValueKind.Number => val.GetInt32(),
            JsonValueKind.String => int.TryParse(val.GetString(), out var i) ? i : defaultValue,
            _ => defaultValue
        };
    }
}
