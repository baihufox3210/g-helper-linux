# IPC control API: implementation scope

The API delegates hardware changes to existing G-Helper controllers. No new hardware backend is introduced.

Source inspection before implementation:

- Performance: `ModeControl.SetPerformanceMode` serializes application; `Modes` tracks configured profiles, including custom profiles. A completed call does not prove every tuning write succeeded.
- GPU: `GPUModeControl.RequestModeSwitch` owns locking, MUX checks, driver checks and reboot workflow. IPC will never invoke `TryReleaseAndSwitch`, bypass checks, or reboot. PCI backend supports Eco/Standard only; Ultimate requires MUX support, Optimized requires the existing feature preference.
- Display: `LinuxDisplayControl` delegates to compositor backends. Existing interfaces expose only the selected laptop/primary panel, not arbitrary displays. Per-display enumeration is unsupported. XWayland fallback will be rejected. Niri works only if an existing native backend probes successfully; no new compositor workaround.
- Battery: `BatteryControl.SetBatteryChargeLimit` clamps and persists even if hardware writes fail. IPC must return hardware readback and distinguish saved settings from observed state.
- Monitoring: reuse existing temperature device reads and fan getters; unknown values are null. No separate sensor discovery.
- Additional control: panel brightness uses the existing minimum brightness protection. Raw fan curves, power limits, voltage, driver release, keyboard effects and backend selection are intentionally outside this initial API.
