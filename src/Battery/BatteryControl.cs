using GHelper.Linux.Helpers;
using GHelper.Linux.Platform;

namespace GHelper.Linux.Battery;

/// <summary>
/// Centralized battery charge-limit management.
/// Handles charge limit read/write, temporary full-charge override,
/// startup re-apply, and 6080-model clamping (models that only accept 60/80/100).
/// </summary>
public static class BatteryControl
{
    // Config keys
    private const string KeyChargeLimit = "charge_limit";
    private const string KeyChargeFull = "charge_full";

    /// <summary>Whether the user has temporarily forced full charge. Persisted in config.</summary>
    public static bool ChargeFull
    {
        get => AppConfig.Is(KeyChargeFull);
        set => AppConfig.Set(KeyChargeFull, value ? 1 : 0);
    }

    /// <summary>Toggle between "charge to saved limit" and "charge to 100%".</summary>
    public static void ToggleBatteryLimitFull()
    {
        if (ChargeFull)
        {
            ChargeFull = false;
            AutoBattery();
        }
        else
        {
            SetBatteryLimitFull();
        }
    }

    /// <summary>Temporarily override charge limit to 100%.</summary>
    public static void SetBatteryLimitFull()
    {
        ChargeFull = true;
        App.Wmi?.SetBatteryChargeLimit(100);
        Logger.WriteLine("BatteryControl: charge limit temporarily set to 100% (charge_full)");
    }

    /// <summary>Clear the full-charge flag. Called when battery reaches 100%.</summary>
    public static void UnSetBatteryLimitFull()
    {
        ChargeFull = false;
        Logger.WriteLine("BatteryControl: charge_full cleared - battery fully charged");
    }

    /// <summary>
    /// Restore the saved charge limit on startup or mode change.
    /// If full-charge mode is active and not initial boot, applies 100%.
    /// </summary>
    public static void AutoBattery(bool init = false)
    {
        if (ChargeFull && !init)
        {
            SetBatteryLimitFull();
            return;
        }

        int saved = AppConfig.Get(KeyChargeLimit);
        if (saved > 0 && saved <= 100)
        {
            SetBatteryChargeLimit(saved);
        }
    }

    /// <summary>
    /// Sets the charge limit. Clamps to [40,100], handles 6080 models,
    /// writes to hardware, and persists to config.
    /// </summary>
    /// <param name="limit">Desired limit (40-100). -1 to re-read from config.</param>
    /// <returns>The actual limit applied (firmware may clamp).</returns>
    public static int SetBatteryChargeLimit(int limit = -1) => SetBatteryChargeLimit(limit, out _);

    /// <summary>Apply the same validation and persistence, retaining the hardware write result.</summary>
    public static int SetBatteryChargeLimit(int limit, out bool writeSucceeded)
    {
        if (limit < 0)
            limit = AppConfig.Get(KeyChargeLimit, 100);

        limit = Math.Clamp(limit, 40, 100);

        // 6080 models only accept 60/80/100
        if (AppConfig.IsChargeLimit6080())
        {
            if (limit < 70)
                limit = 60;
            else if (limit < 90)
                limit = 80;
            else
                limit = 100;
        }
        // Lenovo conservation fallback has exactly two outcomes: snap the
        // request so the slider never claims an in-between percentage.
        else if (App.Wmi is Platform.Linux.Lenovo.LinuxLenovoWmi lw
            && lw.UsesConservationFallback)
        {
            int cap = Platform.Linux.Lenovo.LinuxLenovoWmi.ConservationCap;
            limit = limit <= cap ? cap : 100;
        }

        bool ok = App.Wmi?.SetBatteryChargeLimit(limit) ?? false;
        writeSucceeded = ok;

        if (ok)
        {
            // Re-read actual (firmware may have clamped further)
            int actual = App.Wmi?.GetBatteryChargeLimit() ?? limit;
            if (actual > 0)
                limit = actual;
        }

        AppConfig.Set(KeyChargeLimit, limit);
        Logger.WriteLine($"BatteryControl: charge limit set to {limit}%");
        return limit;
    }

    /// <summary>Returns the saved charge limit from config, defaulting to 100.</summary>
    public static int GetSavedChargeLimit()
    {
        int val = AppConfig.Get(KeyChargeLimit);
        return val > 0 ? val : 100;
    }
}
