using System.Text.Json.Nodes;
using GHelper.Linux.Display;

namespace GHelper.Linux.Ipc;

/// <summary>Adapters to existing application controllers. No raw hardware writes.</summary>
public sealed partial class GHelperIpcControls(Action toggleOsk, Action refreshPerformance) : IIpcControls
{
    public async Task<JsonObject> ExecuteAsync(string method, JsonObject p)
    {
        if (method == "osk.toggle")
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(toggleOsk);
            return new JsonObject { ["state"] = "applied" };
        }
        // Even when a complete frame was buffered synchronously, never perform hardware work on UI.
        return await Task.Run(() => method switch
        {
            "capabilities.get" => Capabilities(),
            "performance.get" => Performance(),
            "performance.set" => SetPerformance(IpcCommandRouter.String(p, "mode")),
            "gpu.get" => Gpu(),
            "gpu.set" => SetGpu(IpcCommandRouter.String(p, "mode")),
            "display.get" => Display(),
            "display.set" => SetRefresh(p["hz"]!.GetValue<int>()),
            "display.brightness.set" => SetBrightness(p["percent"]!.GetValue<int>()),
            "battery.get" => Battery(),
            "battery.set" => SetBattery(p["limit"]!.GetValue<int>()),
            "monitoring.get" => Monitoring(),
            _ => throw new IpcException("unknown_method", "Unknown method")
        });
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    private static JsonNode? Known(int? value) => value >= 0 ? JsonValue.Create(value.Value) : null;
    private static IpcException Unsupported(string message) => new("unsupported", message);
    private static JsonObject Capabilities() => new()
    {
        ["version"] = 1, ["gpuModes"] = Strings(GpuModes()),
        ["displayRefresh"] = RefreshSupported, ["displayScope"] = "primary_panel",
        ["perDisplayEnumeration"] = false, ["batteryChargeLimit"] = (App.Wmi?.GetBatteryChargeLimit() ?? -1) >= 0,
        ["brightness"] = App.Display is LinuxDisplayControl { HasBacklight: true },
        ["methods"] = Strings(["capabilities.get", "performance.get", "performance.set", "gpu.get", "gpu.set", "display.get", "display.set", "display.brightness.set", "battery.get", "battery.set", "monitoring.get", "osk.toggle"])
    };
}
