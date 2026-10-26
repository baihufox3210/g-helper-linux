using System.Text.Json.Nodes;
using GHelper.Linux.Battery;

namespace GHelper.Linux.Ipc;

public sealed partial class GHelperIpcControls
{
    private static JsonObject Battery() => new()
    {
        ["percentage"] = Known(App.Power?.GetBatteryPercentage()),
        ["onAc"] = App.Power is { } power ? JsonValue.Create(power.IsOnAcPower()) : null,
        ["chargeLimit"] = Known(App.Wmi?.GetBatteryChargeLimit()),
        ["savedChargeLimit"] = BatteryControl.GetSavedChargeLimit(), ["fullChargeOverride"] = BatteryControl.ChargeFull
    };
    private static JsonObject SetBattery(int limit)
    {
        if (App.Wmi == null || App.Wmi.GetBatteryChargeLimit() < 0) throw Unsupported("Battery charge limit unavailable");
        int normalized = BatteryControl.SetBatteryChargeLimit(limit, out bool writeSucceeded);
        int actual = App.Wmi.GetBatteryChargeLimit();
        var result = Battery();
        result["requestedLimit"] = limit;
        result["normalizedLimit"] = normalized;
        result["state"] = writeSucceeded && actual == normalized ? "applied" : "failed";
        return result;
    }
}
