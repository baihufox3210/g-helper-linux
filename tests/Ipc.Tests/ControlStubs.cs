// Test doubles for application services. Real production adapter files are linked by the project.
namespace Avalonia.Threading
{
    public sealed class Dispatcher
    {
        public static Dispatcher UIThread { get; } = new();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }
}
namespace GHelper.Linux
{
    public sealed class App
    {
        public static App? Current { get; set; } = new();
        public static MockWindow? MainWindowInstance = new();
        public int AutoCalls;
        public int AutoThreadId;
        public int AutoTargetHz = 165;
        public Exception? AutoException;
        public bool AutoScreen(bool init = false)
        {
            AutoCalls++;
            AutoThreadId = Environment.CurrentManagedThreadId;
            if (AutoException != null) throw AutoException;
            // Configurable fake outcome; the real application's policy is not duplicated here.
            Display!.SetRefreshRate(AutoTargetHz);
            return Display.GetRefreshRate() == AutoTargetHz;
        }
        public static Mode.ModeControl? Mode = new();
        public static Gpu.GPUModeControl? GpuModeCtrl = new();
        public static Display.LinuxDisplayControl? Display = new();
        public static MockHardware? Wmi = new();
        public static MockPower? Power = new();
    }
    public sealed class MockWindow
    {
        public int Refreshes;
        public void RefreshScreenPublic() => Refreshes++;
    }
    public class MockHardware
    {
        public bool EcoAvailable = true;
        public bool MuxAvailable = true;
        public int Limit = 80;
        public int BatteryWrites;
        public bool BatteryWriteSucceeds = true;
        public bool IsGpuEcoAvailable() => EcoAvailable;
        public bool IsFeatureSupported(string attribute) => MuxAvailable;
        public int GetBatteryChargeLimit() => Limit;
        public bool SetBatteryChargeLimit(int value)
        {
            BatteryWrites++;
            if (BatteryWriteSucceeds) Limit = value;
            return BatteryWriteSucceeds;
        }
        public int FanCount => 2;
        public int GetFanRpm(int index) => index == 0 ? 2000 : 1800;
    }
    public sealed class MockPower
    {
        public bool OnAc = true;
        public bool IsOnAcPower() => OnAc;
        public int GetBatteryPercentage() => 74;
    }
}
namespace GHelper.Linux.Mode
{
    public static class Modes
    {
        public static int Current;
        public static int GetCurrent() => Current;
        public static int GetCurrentBase() => Current <= 2 ? Current : 0;
    }
    public sealed class ModeControl { public void SetPerformanceMode(int mode, bool notify) => Modes.Current = mode; }
}
namespace GHelper.Linux.Gpu
{
    public enum GpuMode { Eco, Standard, Optimized, Ultimate }
    public enum GpuSwitchResult { Applied, Busy, AlreadySet, RebootRequired, DriverBlocking, Deferred, Failed, EcoBlocked, DgpuReenableFailed }
    public sealed class GPUModeControl
    {
        public bool IsSwitchInProgress { get; set; }
        public bool Pending;
        public int Writes;
        public GpuSwitchResult Outcome = GpuSwitchResult.Applied;
        public GpuMode Current = GpuMode.Standard;
        public GpuMode GetCurrentMode() => Current;
        public bool IsPendingReboot() => Pending;
        public GpuSwitchResult RequestModeSwitch(GpuMode target, bool deferIfBusy = true)
        {
            if (deferIfBusy) throw new Exception("IPC must opt out of scheduling on lock contention");
            Writes++;
            if (Outcome == GpuSwitchResult.Applied) Current = target;
            return Outcome;
        }
    }
}
namespace GHelper.Linux.Display
{
    public sealed class XrandrBackend { }
    public static class DisplayBackendFactory
    {
        public static bool Wayland;
        public static bool IsWaylandSession() => Wayland;
    }
    public sealed class LinuxDisplayControl
    {
        public object? Backend { get; set; } = new object();
        public bool HasBacklight { get; set; } = true;
        public int Hz = 60;
        public int Writes;
        public bool WriteSucceeds = true;
        public Exception? WriteException;
        public int WriteThreadId;
        public List<int> Rates = [60, 165];
        public int Brightness = 50;
        public bool RefreshReadable = true;
        public int GetRefreshRate() => RefreshReadable ? Hz : -1;
        public List<int> GetAvailableRefreshRates() => Rates;
        public string GetDisplayName() => "mock-panel";
        public void SetRefreshRate(int hz)
        {
            Writes++;
            WriteThreadId = Environment.CurrentManagedThreadId;
            if (WriteException != null) throw WriteException;
            if (WriteSucceeds) Hz = hz;
        }
        public int GetBrightness() => Brightness;
        public void SetBrightness(int value) => Brightness = value;
    }
}
namespace GHelper.Linux.Helpers
{
    public static class AppConfig
    {
        private static readonly Dictionary<string, int> Values = new();
        public static bool Is(string key) => Get(key) == 1;
        public static int Get(string key, int fallback = 0) => Values.GetValueOrDefault(key, fallback);
        public static void Set(string key, int value) => Values[key] = value;
        public static bool IsChargeLimit6080() => true;
        public static bool Pci;
        public static bool Optimized = true;
        public static bool IsPciGpuBackend() => Pci;
        public static bool IsOptimizedGpuModeEnabled() => Optimized;
        public static string? GetString(string key) => null;
    }
    public static class TraySystemMonitor { public static (int, int) ReadTemperatures() => (55, -1); }
}
namespace GHelper.Linux.Platform.Linux
{
    public static class AsusAttributes { public const string GpuMuxMode = "gpu_mux_mode"; }
}

namespace GHelper.Linux.Platform.Linux.Lenovo
{
    public class LinuxLenovoWmi : GHelper.Linux.MockHardware
    {
        public bool UsesConservationFallback => true;
        public const int ConservationCap = 80;
    }
}
