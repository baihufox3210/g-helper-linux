using System.Text.Json.Nodes;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Ipc;

public sealed partial class GHelperIpcControls
{
    private static JsonObject Monitoring()
    {
        var (cpu, gpu) = TraySystemMonitor.ReadTemperatures();
        return new JsonObject
        {
            ["cpuTemperatureC"] = Known(cpu > 0 ? cpu : -1),
            ["gpuTemperatureC"] = Known(gpu > 0 ? gpu : -1),
            ["cpuFanRpm"] = Fan(0), ["gpuFanRpm"] = Fan(1), ["midFanRpm"] = Fan(2),
            ["performance"] = Performance(), ["gpu"] = App.GpuModeCtrl == null ? null : Gpu(),
            ["display"] = RefreshSupported ? Display() : null
        };
    }
    private static JsonNode? Fan(int index) => App.Wmi is { } w && w.FanCount > index ? Known(w.GetFanRpm(index)) : null;
}
