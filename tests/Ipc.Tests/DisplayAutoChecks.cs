using System.Text.Json.Nodes;
using GHelper.Linux;
using GHelper.Linux.Display;
using GHelper.Linux.Helpers;
using GHelper.Linux.Ipc;

internal static class DisplayAutoChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        App.Current = new App();
        App.Display = new LinuxDisplayControl();
        App.Power = new MockPower();
        AppConfig.Set("screen_auto", 0);
        DisplayBackendFactory.Wayland = false;
        var app = App.Current;
        var display = App.Display;
        var router = new IpcCommandRouter(new GHelperIpcControls(() => { }, () => { }));
        async Task<JsonNode> Send(string method, JsonObject? p = null) => JsonNode.Parse(await router.HandleAsync(
            new JsonObject { ["id"] = "auto", ["method"] = method, ["params"] = p ?? new() }.ToJsonString()))!;
        Task<JsonNode> Set(bool enabled) => Send("display.auto.set", new() { ["enabled"] = enabled });
        static string State(JsonNode response) => response["result"]!["state"]!.GetValue<string>();
        static bool Enabled(JsonNode response) => response["result"]!["enabled"]!.GetValue<bool>();
        static string Error(JsonNode response) => response["error"]!["code"]!.GetValue<string>();

        var caps = (await Send("capabilities.get"))["result"]!;
        check(caps["displayAuto"]!.GetValue<bool>(), "Auto capability available with native backend");
        foreach (string method in new[] { "display.auto.get", "display.auto.set", "display.get", "display.set", "display.brightness.set" })
            check(caps["methods"]!.AsArray().Any(n => n!.GetValue<string>() == method), "Capability method: " + method);
        var status = await Send("display.auto.get");
        check(!Enabled(status) && status["result"]!["onAc"]!.GetValue<bool>() && status["result"]!["currentHz"]!.GetValue<int>() == 60, "Auto query on AC");
        App.Power.OnAc = false;
        check(!(await Send("display.auto.get"))["result"]!["onAc"]!.GetValue<bool>(), "Auto query on battery");
        App.Power = null;
        display.Hz = -1;
        status = await Send("display.auto.get");
        check(status["result"]!["onAc"] == null && status["result"]!["currentHz"] == null, "Unavailable power and refresh reported as null");
        check(app.AutoCalls == 0 && display.Writes == 0, "Auto queries perform no mutations");
        App.Power = new MockPower();
        display.Hz = 60;

        var enabled = await Set(true);
        check(Enabled(enabled) && State(enabled) == "applied" && display.Hz == 165 && app.AutoCalls == 1, "Enable immediately delegates to AutoScreen");
        await Set(true);
        check(app.AutoCalls == 2, "Repeated enable reapplies existing policy");
        int writes = display.Writes;
        var disabled = await Set(false);
        check(!Enabled(disabled) && State(disabled) == "applied" && display.Hz == 165, "Disable preserves current rate");
        check(display.Writes == writes && app.AutoCalls == 2, "Disable never calls AutoScreen or writes rate");
        await Set(false);
        check(display.Writes == writes, "Repeated disable remains write-free");
        App.Power.OnAc = false;
        app.AutoTargetHz = 60; // Simulate the policy owner's battery target.
        check(State(await Set(true)) == "applied" && display.Hz == 60 && app.AutoCalls == 3, "Battery enable delegates to same policy owner");

        app.AutoTargetHz = 165;
        display.WriteSucceeds = false;
        var failed = await Set(true);
        check(failed["ok"]!.GetValue<bool>() && State(failed) == "failed" && Enabled(failed), "Failed Auto switch reports failure while preserving saved preference");
        check(failed["result"]!["currentHz"]!.GetValue<int>() == 60, "Failed Auto switch reports observed rate");
        display.RefreshReadable = false;
        failed = await Set(true);
        check(State(failed) == "failed" && failed["result"]!["currentHz"] == null, "Unknown refresh readback cannot claim Auto applied");
        display.RefreshReadable = true;
        app.AutoException = new IOException("private display error");
        check(Error(await Set(true)) == "internal_error", "Auto exception uses protocol error envelope");
        app.AutoException = null;
        display.WriteSucceeds = true;

        AppConfig.Set("screen_auto", 1);
        writes = display.Writes;
        check(Error(await Send("display.set", new() { ["hz"] = 240 })) == "invalid_request" && AppConfig.Is("screen_auto") && display.Writes == writes, "Unadvertised manual rate preserves Auto");
        check(Error(await Send("display.auto.set", new() { ["enabled"] = "true" })) == "invalid_request" && AppConfig.Is("screen_auto"), "Invalid Auto request preserves config");
        var manual = await Send("display.set", new() { ["hz"] = 165 });
        check(State(manual) == "applied" && !AppConfig.Is("screen_auto") && display.Hz == 165, "Manual refresh disables Auto and applies advertised rate");
        check(manual["result"]!["scope"]!.GetValue<string>() == "primary_panel" && manual["result"]!["availableHz"]!.AsArray().Count == 2, "Manual response format preserved");
        AppConfig.Set("screen_auto", 1);
        display.WriteSucceeds = false;
        check(State(await Send("display.set", new() { ["hz"] = 60 })) == "failed" && !AppConfig.Is("screen_auto"), "Failed manual refresh still disables Auto like UI");
        AppConfig.Set("screen_auto", 1);
        display.WriteException = new IOException("private display error");
        check(Error(await Send("display.set", new() { ["hz"] = 60 })) == "internal_error" && !AppConfig.Is("screen_auto"), "Manual exception preserves explicit Auto disable");
        display.WriteException = null;
        display.WriteSucceeds = true;
        await Send("display.brightness.set", new() { ["percent"] = 70 });
        check(display.Brightness == 70, "Brightness setter retained");
        AppConfig.Set("screen_auto", 1);
        await Send("display.brightness.set", new() { ["percent"] = 50 });
        check(AppConfig.Is("screen_auto"), "Brightness does not disable Auto");
        var snapshot = (await Send("display.get"))["result"]!;
        check(snapshot["currentHz"]!.GetValue<int>() == 165 && snapshot["brightnessPercent"]!.GetValue<int>() == 50, "Display getter retained");

        // Unsupported gates must run before setting preferences or touching hardware.
        foreach (string backend in new[] { "missing_display", "missing_backend", "xwayland", "missing_app" })
        {
            App.Display = backend == "missing_display" ? null : display;
            display.Backend = backend == "missing_backend" ? null : backend == "xwayland" ? new XrandrBackend() : new object();
            DisplayBackendFactory.Wayland = backend == "xwayland";
            App.Current = backend == "missing_app" ? null : app;
            writes = display.Writes;
            int calls = app.AutoCalls;
            check(!(await Send("capabilities.get"))["result"]!["displayAuto"]!.GetValue<bool>(), "Unavailable Auto capability: " + backend);
            check(Error(await Send("display.auto.get")) == "unsupported", "Unsupported Auto query: " + backend);
            check(Error(await Set(true)) == "unsupported" && Error(await Set(false)) == "unsupported", "Unsupported Auto mutations: " + backend);
            check(AppConfig.Is("screen_auto") && display.Writes == writes && app.AutoCalls == calls, "Unsupported Auto leaves state intact: " + backend);
            if (backend != "missing_app")
            {
                check(Error(await Send("display.set", new() { ["hz"] = 60 })) == "unsupported" && AppConfig.Is("screen_auto"), "Unsupported manual set preserves Auto: " + backend);
                check(Error(await Send("display.get")) == "unsupported", "Unsupported display query retained: " + backend);
            }
        }
        App.Current = app;
        App.Display = display;
        display.Backend = new object();
        DisplayBackendFactory.Wayland = false;

        // Use a dedicated caller thread so Task.Run cannot be confused with an async continuation.
        int callerThread = 0;
        Exception? threadError = null;
        string? threadResponse = null;
        var caller = new Thread(() =>
        {
            try
            {
                callerThread = Environment.CurrentManagedThreadId;
                threadResponse = router.HandleAsync("{\"method\":\"display.auto.set\",\"params\":{\"enabled\":true}}").GetAwaiter().GetResult();
            }
            catch (Exception ex) { threadError = ex; }
        });
        caller.Start();
        check(caller.Join(TimeSpan.FromSeconds(10)), "Auto request finishes on worker");
        check(threadError == null && JsonNode.Parse(threadResponse!)!["ok"]!.GetValue<bool>(), "Worker request succeeds");
        check(app.AutoThreadId != callerThread && display.WriteThreadId != callerThread, "Auto policy and display writes execute off IPC caller thread");
        check(App.MainWindowInstance!.Refreshes > 0, "Auto/manual settings refresh native screen UI");
    }
}
