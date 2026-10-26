using System.Text.Json.Nodes;
using GHelper.Linux;
using GHelper.Linux.Display;
using GHelper.Linux.Gpu;
using GHelper.Linux.Helpers;
using GHelper.Linux.Ipc;

internal static class AdapterChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var router = new IpcCommandRouter(new GHelperIpcControls(() => { }, () => { }));
        async Task<JsonNode> Send(string method, JsonObject? p = null) => JsonNode.Parse(await router.HandleAsync(new JsonObject
        { ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString()))!;
        foreach (var (outcome, state) in new[]
        {
            (GpuSwitchResult.Applied, "applied"), (GpuSwitchResult.AlreadySet, "already_set"),
            (GpuSwitchResult.Deferred, "pending"), (GpuSwitchResult.RebootRequired, "reboot_required"),
            (GpuSwitchResult.DriverBlocking, "blocked"), (GpuSwitchResult.EcoBlocked, "blocked"),
            (GpuSwitchResult.DgpuReenableFailed, "failed"), (GpuSwitchResult.Failed, "failed"), (GpuSwitchResult.Busy, "busy")
        })
        {
            App.GpuModeCtrl!.Outcome = outcome;
            var response = await Send("gpu.set", new() { ["mode"] = "eco" });
            check(response["result"]!["state"]!.GetValue<string>() == state, $"GPU outcome {outcome}");
            if (outcome is GpuSwitchResult.RebootRequired or GpuSwitchResult.Deferred or GpuSwitchResult.DgpuReenableFailed)
                check(response["result"]!["rebootRequired"]!.GetValue<bool>(), "Reboot flag");
        }
        int writes = App.GpuModeCtrl!.Writes;
        AppConfig.Pci = true;
        check((await Send("gpu.set", new() { ["mode"] = "ultimate" }))["error"]!["code"]!.GetValue<string>() == "unsupported", "PCI has no Ultimate");
        AppConfig.Pci = false;
        App.Wmi!.MuxAvailable = false;
        await Send("gpu.set", new() { ["mode"] = "ultimate" });
        AppConfig.Optimized = false;
        await Send("gpu.set", new() { ["mode"] = "optimized" });
        App.GpuModeCtrl.IsSwitchInProgress = true;
        check((await Send("gpu.set", new() { ["mode"] = "eco" }))["error"]!["code"]!.GetValue<string>() == "busy", "Active GPU transition");
        check(App.GpuModeCtrl.Writes == writes, "Unsupported and busy modes never reach GPU writes");
        App.GpuModeCtrl.IsSwitchInProgress = false;
        DisplayBackendFactory.Wayland = true;
        App.Display!.Backend = new XrandrBackend();
        check((await Send("display.set", new() { ["hz"] = 60 }))["error"]!["code"]!.GetValue<string>() == "unsupported", "XWayland refresh blocked");
        check(App.Display.Writes == 0, "XWayland no write");
        App.Display.Backend = new object();
        check((await Send("display.set", new() { ["hz"] = 240 }))["error"]!["code"]!.GetValue<string>() == "invalid_request", "Reject unadvertised rate");
        check(App.Display.Writes == 0, "Unadvertised rate no write");
        check((await Send("display.set", new() { ["hz"] = 165 }))["result"]!["state"]!.GetValue<string>() == "applied", "Discovered rate applied");
        App.Display.WriteSucceeds = false;
        check((await Send("display.set", new() { ["hz"] = 60 }))["result"]!["state"]!.GetValue<string>() == "failed", "Refresh failure readback");
        check((await Send("battery.set", new() { ["limit"] = 65 }))["result"]!["chargeLimit"]!.GetValue<int>() == 60, "Battery normalization");
        App.Wmi.BatteryWriteSucceeds = false;
        check((await Send("battery.set", new() { ["limit"] = 100 }))["result"]!["state"]!.GetValue<string>() == "failed", "Battery failure readback");
        check((await Send("battery.set", new() { ["limit"] = 60 }))["result"]!["state"]!.GetValue<string>() == "failed", "Failed battery write cannot claim success from unchanged readback");
        App.Wmi.Limit = -1;
        int batteryWrites = App.Wmi.BatteryWrites;
        check((await Send("battery.set", new() { ["limit"] = 80 }))["error"]!["code"]!.GetValue<string>() == "unsupported", "Unsupported battery");
        check(App.Wmi.BatteryWrites == batteryWrites, "Unsupported battery no write");
        var monitor = (await Send("monitoring.get"))["result"]!;
        check(monitor["gpuTemperatureC"] == null && monitor["midFanRpm"] == null, "Unavailable sensors null");
        check(monitor["cpuFanRpm"]!.GetValue<int>() == 2000, "RPM retained");
        await Send("performance.set", new() { ["mode"] = "silent" });
        check((await Send("performance.get"))["result"]!["mode"]!.GetValue<string>() == "silent", "Performance delegation");
    }
}
