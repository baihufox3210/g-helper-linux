using System.Text.Json;
using System.Text.Json.Nodes;

namespace GHelper.Linux.Ipc;

public sealed class IpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IIpcControls
{
    Task<JsonObject> ExecuteAsync(string method, JsonObject parameters);
}

/// <summary>Strict protocol boundary, independent of UI and hardware.</summary>
public sealed class IpcCommandRouter(IIpcControls controls, Action<Exception>? onError = null)
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    public async Task<string> HandleAsync(string line)
    {
        JsonNode? id = null;
        try
        {
            string method;
            JsonObject p;
            if (!line.TrimStart().StartsWith('{'))
            {
                (method, p) = line.Trim() switch
                {
                    "toggle-osk" => ("osk.toggle", new JsonObject()),
                    "profile-silent" => ("performance.set", new JsonObject { ["mode"] = "silent" }),
                    "profile-balanced" => ("performance.set", new JsonObject { ["mode"] = "balanced" }),
                    "profile-turbo" => ("performance.set", new JsonObject { ["mode"] = "turbo" }),
                    _ => throw new IpcException("unknown_method", "Unknown command")
                };
            }
            else
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                RejectDuplicates(doc.RootElement);
                var request = JsonNode.Parse(line) as JsonObject ?? throw Invalid("Expected an object");
                Only(request, "id", "version", "method", "params");
                if (request["id"] is { } requestId)
                {
                    if (requestId is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length > 128)
                        throw Invalid("id must be a string of at most 128 characters");
                    id = requestId.DeepClone();
                }
                if (request.ContainsKey("version") && (!TryInt(request["version"], out int version) || version != 1))
                    throw new IpcException("unsupported_version", "Only version 1 is supported");
                method = String(request, "method");
                p = request["params"] as JsonObject ?? (request.ContainsKey("params") ? throw Invalid("params must be an object") : new JsonObject());
            }
            Validate(method, p);
            // Do not queue stale writes or let polling compete with a transition.
            if (!await _operation.WaitAsync(0)) throw new IpcException("busy", "Another IPC operation is in progress");
            JsonObject result;
            try { result = await controls.ExecuteAsync(method, p); }
            finally { _operation.Release(); }
            return new JsonObject { ["version"] = 1, ["id"] = id, ["ok"] = true, ["result"] = result }.ToJsonString();
        }
        catch (Exception ex)
        {
            if (ex is not (IpcException or JsonException)) onError?.Invoke(ex);
            string code = ex switch { IpcException e => e.Code, JsonException => "invalid_request", _ => "internal_error" };
            return Error(id, code, ex is IpcException ? ex.Message : code == "invalid_request" ? "Malformed JSON" : "Operation failed; inspect G-Helper logs");
        }
    }

    public static string Error(JsonNode? id, string code, string message) => new JsonObject
    {
        ["version"] = 1, ["id"] = id, ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    }.ToJsonString();

    private static void Validate(string method, JsonObject p)
    {
        switch (method)
        {
            case "performance.set":
                Only(p, "mode");
                if (String(p, "mode") is not ("silent" or "balanced" or "turbo")) throw Invalid("Invalid performance mode");
                break;
            case "gpu.set":
                Only(p, "mode");
                if (String(p, "mode") is not ("eco" or "standard" or "optimized" or "ultimate")) throw Invalid("Invalid GPU mode");
                break;
            case "display.auto.set": Only(p, "enabled"); Boolean(p, "enabled"); break;
            case "display.set": Only(p, "hz"); Integer(p, "hz", 1, 1000); break;
            case "display.brightness.set": Only(p, "percent"); Integer(p, "percent", 4, 100); break;
            case "battery.set": Only(p, "limit"); Integer(p, "limit", 40, 100); break;
            case "capabilities.get": case "performance.get": case "gpu.get": case "display.get": case "display.auto.get":
            case "battery.get": case "monitoring.get": case "osk.toggle": Only(p); break;
            default: throw new IpcException("unknown_method", "Unknown method");
        }
    }
    private static void RejectDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>();
            foreach (var property in e.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw Invalid("Duplicate property");
                RejectDuplicates(property.Value);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) RejectDuplicates(item);
    }
    private static void Only(JsonObject p, params string[] names)
    {
        foreach (var key in p.Select(k => k.Key)) if (!names.Contains(key)) throw Invalid($"Unexpected parameter: {key}");
    }
    public static string String(JsonObject p, string key) => p[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Invalid($"{key} must be a string");
    public static bool Boolean(JsonObject p, string key) => p[key] is JsonValue v && v.TryGetValue<bool>(out bool value) ? value : throw Invalid($"{key} must be a boolean");
    private static bool TryInt(JsonNode? n, out int value) { value = 0; return n is JsonValue v && v.TryGetValue<int>(out value); }
    public static int Integer(JsonObject p, string key, int min, int max) => TryInt(p[key], out int n) && n >= min && n <= max ? n : throw Invalid($"{key} must be an integer from {min} to {max}");
    private static IpcException Invalid(string message) => new("invalid_request", message);
}
