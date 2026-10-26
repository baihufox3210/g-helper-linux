using System.Text.Json.Nodes;
using GHelper.Linux.Gpu;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Ipc;

public sealed partial class GHelperIpcControls
{
    private static List<string> GpuModes()
    {
        var wmi = App.Wmi;
        var modes = new List<string>();
        if (wmi == null || App.GpuModeCtrl == null || !wmi.IsGpuEcoAvailable()) return modes;
        modes.AddRange(["eco", "standard"]);
        if (!AppConfig.IsPciGpuBackend())
        {
            if (AppConfig.IsOptimizedGpuModeEnabled()) modes.Add("optimized");
            if (wmi.IsFeatureSupported(AsusAttributes.GpuMuxMode)) modes.Add("ultimate");
        }
        return modes;
    }
    private static JsonObject Gpu()
    {
        var control = App.GpuModeCtrl ?? throw Unsupported("GPU controller unavailable");
        return new JsonObject
        {
            ["mode"] = control.GetCurrentMode().ToString().ToLowerInvariant(),
            ["supportedModes"] = Strings(GpuModes()), ["switchInProgress"] = control.IsSwitchInProgress,
            ["rebootRequired"] = control.IsPendingReboot(), ["configuredMode"] = AppConfig.GetString("gpu_mode")
        };
    }
    private static JsonObject SetGpu(string mode)
    {
        if (!GpuModes().Contains(mode)) throw Unsupported("GPU mode is not supported by the current backend");
        var control = App.GpuModeCtrl!;
        if (control.IsSwitchInProgress) throw new IpcException("busy", "GPU switch in progress");
        var outcome = control.RequestModeSwitch(mode switch
        {
            "eco" => GpuMode.Eco, "standard" => GpuMode.Standard,
            "optimized" => GpuMode.Optimized, _ => GpuMode.Ultimate
        }, deferIfBusy: false);
        var result = Gpu();
        result["requestedMode"] = mode;
        result["outcome"] = outcome.ToString();
        result["state"] = outcome switch
        {
            GpuSwitchResult.Busy => "busy",
            GpuSwitchResult.Applied => "applied", GpuSwitchResult.AlreadySet => "already_set",
            GpuSwitchResult.RebootRequired => "reboot_required", GpuSwitchResult.Deferred => "pending",
            GpuSwitchResult.DriverBlocking => "blocked", GpuSwitchResult.EcoBlocked => "blocked",
            _ => "failed"
        };
        result["rebootRequired"] = control.IsPendingReboot() || outcome is GpuSwitchResult.RebootRequired or GpuSwitchResult.Deferred or GpuSwitchResult.DgpuReenableFailed;
        return result;
    }

}
