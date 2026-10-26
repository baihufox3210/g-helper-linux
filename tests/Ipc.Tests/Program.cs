using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using GHelper.Linux.Helpers;
using GHelper.Linux.Ipc;

int checks = 0;
void Check(bool condition, string description)
{
    checks++;
    if (!condition) throw new Exception(description);
}
var fake = new MockControls();
var router = new IpcCommandRouter(fake);
async Task<JsonObject> Request(string request) => JsonNode.Parse(await router.HandleAsync(request))!.AsObject();
foreach (var (request, error) in new[]
{
    ("{", "invalid_request"),
    ("[]", "unknown_method"),
    ("{\"method\":\"gpu.set\",\"params\":{\"mode\":\"force\"}}", "invalid_request"),
    ("{\"method\":\"gpu.set\",\"params\":{\"mode\":\"eco\",\"force\":true}}", "invalid_request"),
    ("{\"method\":\"battery.set\",\"params\":{\"limit\":39}}", "invalid_request"),
    ("{\"method\":\"battery.set\",\"params\":{\"limit\":101}}", "invalid_request"),
    ("{\"method\":\"battery.set\",\"params\":{\"limit\":\"80\"}}", "invalid_request"),
    ("{\"method\":\"display.set\",\"params\":{\"hz\":60.5}}", "invalid_request"),
    ("{\"method\":\"display.brightness.set\",\"params\":{\"percent\":0}}", "invalid_request"),
    ("{\"method\":\"gpu.get\",\"method\":\"gpu.set\"}", "invalid_request"),
    ("{\"method\":\"gpu.get\",\"params\":null}", "invalid_request"),
    ("{\"method\":\"gpu.get\",\"version\":2}", "unsupported_version"),
    ("{\"method\":\"gpu.get\",\"id\":5}", "invalid_request"),
    ("{\"method\":\"exec\"}", "unknown_method"),
})
{
    int calls = fake.Calls;
    var response = await Request(request);
    Check(response["ok"]!.GetValue<bool>() == false && response["error"]!["code"]!.GetValue<string>() == error, request);
    Check(fake.Calls == calls, "Validation must precede dispatch");
}
// Auto accepts only an explicit JSON boolean, with no extra fields.
foreach (string parameters in new[] { "{}", "{\"enabled\":null}", "{\"enabled\":1}",
    "{\"enabled\":0}", "{\"enabled\":\"true\"}", "{\"enabled\":[]}", "{\"enabled\":{}}",
    "{\"enabled\":true,\"hz\":60}", "{\"enabled\":true,\"enabled\":false}" })
{
    int calls = fake.Calls;
    var response = await Request("{\"method\":\"display.auto.set\",\"params\":" + parameters + "}");
    Check(response["error"]!["code"]!.GetValue<string>() == "invalid_request", "Auto parameter validation: " + parameters);
    Check(fake.Calls == calls, "Invalid Auto request never dispatched");
}
Check((await Request("{\"method\":\"display.auto.get\",\"params\":{\"enabled\":true}}"))["error"]!["code"]!.GetValue<string>() == "invalid_request", "Auto get takes no parameters");
foreach (bool enabled in new[] { true, false })
{
    await Request(new JsonObject { ["method"] = "display.auto.set", ["params"] = new JsonObject { ["enabled"] = enabled } }.ToJsonString());
    Check(fake.Method == "display.auto.set" && fake.Parameters!["enabled"]!.GetValue<bool>() == enabled, "Auto boolean forwarded");
}
await Request("{\"method\":\"display.auto.get\"}");
Check(fake.Method == "display.auto.get", "Auto query registered");

foreach (var mode in new[] { "silent", "balanced", "turbo" })
{
    var response = await Request("profile-" + mode);
    Check(response["ok"]!.GetValue<bool>() && fake.Method == "performance.set" && fake.Parameters?["mode"]!.GetValue<string>() == mode, "Legacy profile");
}
await Request("toggle-osk");
Check(fake.Method == "osk.toggle", "Legacy OSK");
foreach (string state in new[] { "applied", "pending", "reboot_required", "blocked", "failed" })
{
    fake.State = state;
    var response = await Request("{\"id\":\"test\",\"version\":1,\"method\":\"gpu.set\",\"params\":{\"mode\":\"eco\"}}");
    Check(response["id"]!.GetValue<string>() == "test" && response["result"]!["state"]!.GetValue<string>() == state, "State and id preserved");
}
fake.Error = new IpcException("unsupported", "not available");
Check((await Request("{\"method\":\"display.get\"}"))["error"]!["code"]!.GetValue<string>() == "unsupported", "Unsupported");
fake.Error = new Exception("private detail");
Check(!(await router.HandleAsync("{\"method\":\"gpu.get\"}")).Contains("private detail"), "Internal errors sanitized");
fake.Error = null;
fake.Gate = new TaskCompletionSource();
var pending = Request("profile-silent");
Check((await Request("profile-turbo"))["error"]!["code"]!.GetValue<string>() == "busy", "Concurrent changes rejected");
fake.Gate.SetResult();
await pending;
fake.Gate = null;
fake.Gate = new TaskCompletionSource();
var pendingAuto = Request("{\"method\":\"display.auto.set\",\"params\":{\"enabled\":true}}");
Check((await Request("{\"method\":\"display.auto.get\"}"))["error"]!["code"]!.GetValue<string>() == "busy", "Auto polling cannot race IPC transition");
Check((await Request("{\"method\":\"display.set\",\"params\":{\"hz\":60}}"))["error"]!["code"]!.GetValue<string>() == "busy", "Manual IPC change cannot race Auto transition");
fake.Gate.SetResult();
await pendingAuto;
fake.Gate = null;

// Real Unix socket + mocked controls. No App or hardware controller is loaded.
string runtime = Path.Combine(Path.GetTempPath(), "ghelper-ipc-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
string? oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
try
{
    CommandIpc.StartServer(router.HandleAsync);
    string path = Path.Combine(runtime, "ghelper.sock");
    Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Socket is 0600");
    async Task<string> Exchange(byte[] bytes, bool fragmented = false)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        using var stream = new NetworkStream(socket);
        if (fragmented) foreach (byte b in bytes) await stream.WriteAsync(new byte[] { b });
        else await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "";
    }
    var result = JsonNode.Parse(await Exchange(Encoding.UTF8.GetBytes("{\"method\":\"performance.get\",\"id\":\"wire\"}\n"), true))!;
    Check(result["id"]!.GetValue<string>() == "wire", "Fragmented frame round trip");
    var large = JsonNode.Parse(await Exchange(Encoding.UTF8.GetBytes(new string('x', CommandIpc.MaxRequestBytes + 1))))!;
    Check(large["error"]!["code"]!.GetValue<string>() == "request_too_large", "Bounded frame");
    var invalid = JsonNode.Parse(await Exchange(new byte[] { 0xff, 10 }))!;
    Check(invalid["error"]!["code"]!.GetValue<string>() == "invalid_request", "Strict UTF-8");
    Check(JsonNode.Parse(await Exchange(Encoding.UTF8.GetBytes("toggle-osk\n")))!["ok"]!.GetValue<bool>(), "Listener survives malformed clients");
    using (var idle = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
    {
        await idle.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await idle.SendAsync(Encoding.UTF8.GetBytes("{"));
        var byteBuffer = new byte[1];
        int read = await idle.ReceiveAsync(byteBuffer.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
        Check(read == 0, "Incomplete frames time out and close");
    }
    Check(JsonNode.Parse(await Exchange(Encoding.UTF8.GetBytes("toggle-osk\n")))!["ok"]!.GetValue<bool>(), "Listener survives timed-out clients");
    CommandIpc.Stop();
    File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
    CommandIpc.StartServer(router.HandleAsync);
    Check(!File.Exists(path), "Shared runtime directory rejected");
    File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    string link = runtime + "-link";
    Directory.CreateSymbolicLink(link, runtime);
    Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", link);
    try
    {
        CommandIpc.StartServer(router.HandleAsync);
        Check(!File.Exists(path), "Symlink runtime directory rejected");
    }
    finally { Directory.Delete(link); }

}
finally
{
    CommandIpc.Stop();
    Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", oldRuntime);
    Directory.Delete(runtime, true);
}
await AdapterChecks.Run(Check);
await DisplayAutoChecks.Run(Check);
Console.WriteLine($"PASS: {checks} IPC checks (no hardware access)");

sealed class MockControls : IIpcControls
{
    public int Calls;
    public string? Method;
    public JsonObject? Parameters;
    public string State = "applied";
    public Exception? Error;
    public TaskCompletionSource? Gate;
    public async Task<JsonObject> ExecuteAsync(string method, JsonObject parameters)
    {
        Calls++; Method = method; Parameters = parameters;
        if (Error != null) throw Error;
        if (Gate != null) await Gate.Task;
        return new JsonObject { ["state"] = State };
    }
}
