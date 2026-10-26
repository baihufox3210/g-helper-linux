# IPC control API: implementation scope

The API delegates hardware changes to existing G-Helper controllers. No new hardware backend is introduced.

Source inspection before implementation:

- Performance: `ModeControl.SetPerformanceMode` serializes application; `Modes` tracks configured profiles, including custom profiles. A completed call does not prove every tuning write succeeded.
- GPU: `GPUModeControl.RequestModeSwitch` owns locking, MUX checks, driver checks and reboot workflow. IPC will never invoke `TryReleaseAndSwitch`, bypass checks, or reboot. PCI backend supports Eco/Standard only; Ultimate requires MUX support, Optimized requires the existing feature preference.
- Display: `LinuxDisplayControl` delegates to compositor backends. Existing interfaces expose only the selected laptop/primary panel, not arbitrary displays. Per-display enumeration is unsupported. XWayland fallback will be rejected. Niri works only if an existing native backend probes successfully; no new compositor workaround.
- Battery: `BatteryControl.SetBatteryChargeLimit` clamps and persists even if hardware writes fail. IPC must return hardware readback and distinguish saved settings from observed state.
- Monitoring: reuse existing temperature device reads and fan getters; unknown values are null. No separate sensor discovery.
- Additional control: panel brightness uses the existing minimum brightness protection. Raw fan curves, power limits, voltage, driver release, keyboard effects and backend selection are intentionally outside this initial API.

## Transport and security

Connect as the logged-in user to `$XDG_RUNTIME_DIR/ghelper.sock`. G-Helper must already be running. The directory must exist, belong to the current effective UID, have no group/other permissions, and not itself be a symlink. There is no shared `/tmp` fallback. The Linux `statx` ownership check fails closed if unavailable. The socket is `0600`, and both server and bundled clients verify Linux `SO_PEERCRED`. No external client needs root. Existing G-Helper helpers/polkit authorization may still be needed for writes.

Send **one UTF-8 JSON object followed by newline per connection**; read one newline-terminated response, then close. Maximum request size is 8192 bytes **including newline**. Incomplete frames are discarded after 10 seconds; writes time out after 10 seconds. At most eight clients are accepted concurrently; excess clients are disconnected. Complete operations are serialized by the router and concurrent requests return `busy`, including queries. UI callers retain the existing controller synchronization. IPC GPU calls atomically return `Busy` on controller lock contention without scheduling a reboot change.

Hardware work runs off the UI thread. In-flight hardware operations are not cancelled when a client disconnects or times out: cancelling a wait does not undo a hardware write. GPU helpers may take minutes or display authorization prompts. Clients should use a generous response timeout (the example defaults to 300 seconds). After a timeout, query status before retrying; a missing response does not mean no change occurred. Stop closes the listener and client network waits; already-running controller calls can finish.

## Version 1 requests and responses

```json
{"version":1,"id":"1","method":"performance.set","params":{"mode":"balanced"}}
```

`version` defaults to 1 when omitted. `id` is an optional string of up to 128 characters, echoed in the response; absent or unparseable IDs return null. `params` defaults to `{}` when omitted. Unknown fields, duplicate keys, wrong types, noninteger numbers for integer parameters, and invalid ranges are rejected. Method and enum strings are case sensitive. Batches and notifications are not supported.

```json
{"version":1,"id":"1","ok":true,"result":{"mode":"balanced","index":0,"baseMode":"balanced","source":"configured","state":"completed"}}
```

```json
{"version":1,"id":"2","ok":false,"error":{"code":"unsupported","message":"GPU mode is not supported by the current backend"}}
```

`ok` means dispatch completed and a result is available. **Inspect `result.state` for mutation outcomes**: `ok:true` can contain `failed`, `blocked`, or `busy`. Protocol/dispatch failures use `ok:false` and codes `invalid_request`, `unknown_method`, `unsupported_version`, `unsupported`, `busy`, `request_too_large`, or `internal_error`. Invalid UTF-8 uses `invalid_request`. Internal exception details are logged by G-Helper and are not returned. An oversized frame has null ID.

| Method | Parameters | Result / constraints |
|---|---|---|
| `capabilities.get` | none | Registered methods, supported GPU modes, refresh/brightness/charge-limit availability; capabilities can change |
| `performance.get` | none | Configured mode, index, base mode; custom mode reported as `custom` |
| `performance.set` | `mode`: `silent`, `balanced`, `turbo` | Delegates to ModeControl (including existing configured mode hooks); `completed` means the synchronous controller call returned; asynchronous tuning may continue and individual writes are not verified |
| `gpu.get` | none | Current mode, configured target, supported modes, switch-in-progress and reboot flags |
| `gpu.set` | `mode`: `eco`, `standard`, `optimized`, `ultimate` | Uses normal safe controller workflow; unsupported modes rejected before writes |
| `display.get` | none | `scope:primary_panel`, name, current integer Hz, advertised integer Hz array, brightness; no per-display enumeration |
| `display.set` | `hz`: integer 1–1000 | Must also appear in backend's current advertised rates; readback determines `applied` or `failed` |
| `display.brightness.set` | `percent`: integer 4–100 | Existing backlight controller preserves minimum; `completed` plus observed brightness (hardware quantization may differ) |
| `battery.get` | none | Percentage, AC state, observed charge limit, saved limit, temporary full-charge override |
| `battery.set` | `limit`: integer 40–100 | Existing model-specific normalization and persistence; write result and hardware readback determine `applied`/`failed` |
| `monitoring.get` | none | CPU/GPU temperatures in °C, CPU/GPU/mid fans in RPM, performance/GPU/display status; unavailable readings null |
| `osk.toggle` | none | UI-dispatched on-screen keyboard toggle |

GPU results additionally include `requestedMode`, the exact controller `outcome`, observed `mode`, `configuredMode`, and `rebootRequired`:

| Controller outcome | `state` | Meaning |
|---|---|---|
| Applied | `applied` | Controller applied transition |
| AlreadySet | `already_set` | No transition needed |
| RebootRequired | `reboot_required` | MUX change awaits reboot |
| Deferred | `pending` | Existing controller workflow saved a pending change |
| DriverBlocking | `blocked` | Active driver prevents safe switch; use G-Helper UI for explicit release/reboot choices |
| EcoBlocked | `blocked` | Unsafe MUX/Eco combination refused |
| DgpuReenableFailed | `failed` | dGPU recovery failed; reboot advised (`rebootRequired:true`) |
| Failed | `failed` | Controller could not complete request |
| Busy | `busy` | Another controller transition won the lock; no IPC change scheduled |

Blocked GPU requests can still have a pending configuration or MUX change from the controller's safe workflow. Always inspect the returned current/configured modes and reboot flag. IPC never performs driver release, forces a switch, schedules a reboot independently, or reboots the machine. Optimized can remain selected while physical GPU availability follows AC state.

Battery limits can be normalized by firmware (e.g. 60/80/100). Results contain requested, normalized, saved and observed limits. Saved settings can persist even after failure; this preserves existing BatteryControl behavior. A temporary full-charge override is reported and preserved; it may supersede the saved limit during subsequent automatic application.

Refresh capability means a native backend exists, not a guarantee that a particular mode change will succeed. Existing APIs report rounded integer Hz and only the selected panel. External displays, fractional rates, arbitrary connectors, and all-display enumeration are unsupported. On Wayland, an XWayland xrandr fallback is rejected even for queries. Niri without a successfully probed native backend returns `unsupported`; this implementation adds no Niri-specific commands.

Monitoring shares the tray temperature reader and honors dGPU idle/monitoring preferences. A null GPU temperature may intentionally avoid waking the GPU; the existing AMD iGPU fallback can provide the GPU reading. Status is a collection of controller reads, not an atomic snapshot across UI/automatic activity. Poll conservatively (e.g. every three seconds) and back off on `busy`.

## Example client and all methods

`tools/ghelper-ipc.py` uses only Python 3's standard library. It returns exit code 1 for protocol/connection errors and 2 for a `failed`, `blocked`, or `busy` result. Pending/reboot states return 0 and must be displayed by integrations. Examples below that set values change hardware: run only when you intend that change. Discover modes/rates before choosing values; substitute an advertised rate for `HZ`.

```sh
python3 tools/ghelper-ipc.py capabilities.get
python3 tools/ghelper-ipc.py performance.get
python3 tools/ghelper-ipc.py performance.set '{"mode":"silent"}'
python3 tools/ghelper-ipc.py performance.set '{"mode":"balanced"}'
python3 tools/ghelper-ipc.py performance.set '{"mode":"turbo"}'
python3 tools/ghelper-ipc.py gpu.get
python3 tools/ghelper-ipc.py gpu.set '{"mode":"eco"}'
python3 tools/ghelper-ipc.py gpu.set '{"mode":"standard"}'
python3 tools/ghelper-ipc.py gpu.set '{"mode":"optimized"}'
python3 tools/ghelper-ipc.py gpu.set '{"mode":"ultimate"}'
python3 tools/ghelper-ipc.py display.get
python3 tools/ghelper-ipc.py display.set "{\"hz\":${HZ}}"
python3 tools/ghelper-ipc.py display.brightness.set '{"percent":50}'
python3 tools/ghelper-ipc.py battery.get
python3 tools/ghelper-ipc.py battery.set '{"limit":80}'
python3 tools/ghelper-ipc.py monitoring.get
python3 tools/ghelper-ipc.py osk.toggle
```

Existing plain-text `profile-silent`, `profile-balanced`, `profile-turbo`, and `toggle-osk` followed by newline are still accepted on the same socket. They now receive JSON replies; old fire-and-forget clients can ignore them. The existing `ghelper --osk` follow-up invocation remains compatible. Unknown legacy commands return `unknown_method`.

For DMS: discover capabilities, render current status, submit validated requests, and render pending/reboot/blocked states. DMS should never invoke hardware helpers directly. No DMS code or configuration is installed here.

## Build, tests and manual installation

Requires .NET 10 SDK and the repository's documented native build dependencies. Nothing here installs, launches, or restarts G-Helper automatically.

```sh
# Tests load mocks and temporary files/sockets, never the installed app.
dotnet run --project tests/Ipc.Tests -c Debug
dotnet run --project tests/GHelper.Linux.Tests -c Debug
python3 -m unittest discover -s tests/ipc-python -v

# Full repository build (compiles embedded native helpers, then .NET/AOT).
./build.sh
# Faster managed build, using the existing repository configuration:
./build.sh --no-aot
```

For a direct managed compilation, first compile the required embedded helper (build.sh normally does this):

```sh
(cd vendor/gpu-helper && cc -O2 -Wall -Wno-unused-result -DNDEBUG \
  -o gpu-helper gpu-helper.c process_ops.c nvidia_ops.c pci_ops.c \
  wmi_ops.c ec_ops.c msr_ops.c lenovo_ops.c -ldl)
dotnet build src/GHelper.Linux.csproj -c Debug -p:PublishAot=false
```

After reviewing and intentionally deciding to replace the installed version, follow the repository installation guide / `sudo ./install/install-local.sh`. That is a separate manual step and may require privileges. Close the old instance intentionally before launching the new build so the existing single-instance lock and socket remain authoritative. No socket API is available from the new source until that new build is running.

## Verification limits

Automated tests cover strict parsing, validation, legacy aliases, IDs, errors, busy serialization, adapter outcome mapping, capability gates, readback failures, Unix socket permissions, fragmented frames, invalid encoding, and oversized frames. Adapter tests link production adapter code against mock application services; the existing scenario suite separately exercises the real GPU controller with mocked hardware. No hardware-changing command was run for this implementation. Real GPU/MUX/reboot transitions, display refresh under Niri/Wayland, battery firmware, and physical sensors require explicit hardware testing. Prior Silent/Balanced hardware testing reported by the repository owner does not verify this new implementation.

Development verification used .NET SDK 10.0.403: the managed Debug build and the default Release build succeeded. `dotnet build` with the default AOT-enabled project settings was checked; a native `dotnet publish` and the full packaging script were not run. IPC tests passed 78 checks, the repository regression suite passed 163 checks, and the Python client passed four mock-server tests. No installation or restart was performed.
