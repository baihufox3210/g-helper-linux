using System.Text.Json.Nodes;
using GHelper.Linux.Display;
using GHelper.Linux.Helpers;

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
        AppConfig.Set("screen_auto", 0);
        try
        {
            d.SetRefreshRate(hz);
            var result = Display();
            result["state"] = d.GetRefreshRate() == hz ? "applied" : "failed";
            return result;
        }
        finally { RefreshScreenUi(); }
    }
    private static JsonObject DisplayAuto()
    {
        if (!RefreshSupported || App.Current is not App)
            throw Unsupported("Automatic display refresh control unavailable");
        return new JsonObject
        {
            ["enabled"] = AppConfig.Is("screen_auto"),
            ["onAc"] = App.Power is { } power ? JsonValue.Create(power.IsOnAcPower()) : null,
            ["currentHz"] = Known(App.Display!.GetRefreshRate())
        };
    }

    private static JsonObject SetDisplayAuto(bool enabled)
    {
        if (!RefreshSupported || App.Current is not App app)
            throw Unsupported("Automatic display refresh control unavailable");
        AppConfig.Set("screen_auto", enabled ? 1 : 0);
        // ExecuteAsync runs this on a worker. AutoScreen remains the only policy owner.
        // Disabling must not call AutoScreen or issue any display/overdrive writes.
        try
        {
            bool applied = !enabled || app.AutoScreen();
            var result = DisplayAuto();
            result["state"] = applied ? "applied" : "failed";
            return result;
        }
        finally { RefreshScreenUi(); }
    }

    private static void RefreshScreenUi() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        App.MainWindowInstance?.RefreshScreenPublic());

    private static JsonObject SetBrightness(int percent)
    {
        if (App.Display is not LinuxDisplayControl { HasBacklight: true } d) throw Unsupported("Backlight unavailable");
        d.SetBrightness(percent);
        return new JsonObject { ["state"] = "completed", ["brightnessPercent"] = Known(d.GetBrightness()) };
    }

}
