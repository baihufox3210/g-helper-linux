using System.Text.Json.Nodes;
using GHelper.Linux.Mode;

namespace GHelper.Linux.Ipc;

public sealed partial class GHelperIpcControls
{
    private static string ProfileName(int mode) => mode switch { 0 => "balanced", 1 => "turbo", 2 => "silent", _ => "custom" };
    private static JsonObject Performance() => new()
    {
        ["mode"] = ProfileName(Modes.GetCurrent()), ["index"] = Modes.GetCurrent(),
        ["baseMode"] = ProfileName(Modes.GetCurrentBase()), ["source"] = "configured"
    };
    private JsonObject SetPerformance(string mode)
    {
        var control = App.Mode ?? throw Unsupported("Performance controller unavailable");
        control.SetPerformanceMode(mode switch { "balanced" => 0, "turbo" => 1, _ => 2 }, true);
        Avalonia.Threading.Dispatcher.UIThread.Post(refreshPerformance);
        var result = Performance();
        result["state"] = "completed"; // SetPerformanceMode has no aggregate success result.
        return result;
    }

}
