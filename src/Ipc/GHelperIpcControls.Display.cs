using System.Text.Json.Nodes;
using GHelper.Linux.Display;

namespace GHelper.Linux.Ipc;

public sealed partial class GHelperIpcControls
{
    private static bool RefreshSupported => App.Display is LinuxDisplayControl { Backend: not null } d
        && !(DisplayBackendFactory.IsWaylandSession() && d.Backend is XrandrBackend);
    private static JsonObject Display()
    {
        if (!RefreshSupported) throw Unsupported("No native display refresh backend for this session");
        var d = App.Display!;
        return new JsonObject
        {
            ["scope"] = "primary_panel", ["perDisplayEnumeration"] = false,
            ["name"] = d.GetDisplayName(), ["currentHz"] = Known(d.GetRefreshRate()),
            ["availableHz"] = new JsonArray(d.GetAvailableRefreshRates().Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["brightnessPercent"] = Known(d.GetBrightness())
        };
    }
    private static JsonObject SetRefresh(int hz)
    {
        if (!RefreshSupported) throw Unsupported("No native display refresh backend for this session");
        var d = App.Display!;
        if (!d.GetAvailableRefreshRates().Contains(hz)) throw new IpcException("invalid_request", "Refresh rate is not advertised by the current panel");
        d.SetRefreshRate(hz);
        var result = Display();
        result["state"] = d.GetRefreshRate() == hz ? "applied" : "failed";
        return result;
    }
    private static JsonObject SetBrightness(int percent)
    {
        if (App.Display is not LinuxDisplayControl { HasBacklight: true } d) throw Unsupported("Backlight unavailable");
        d.SetBrightness(percent);
        return new JsonObject { ["state"] = "completed", ["brightnessPercent"] = Known(d.GetBrightness()) };
    }

}
