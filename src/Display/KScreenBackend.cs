using System.Text.RegularExpressions;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Display;

/// <summary>
/// Display backend using kscreen-doctor (KDE Wayland).
/// Works on all KDE Plasma Wayland sessions, including Plasma 5.x where wlr-randr is unsupported.
/// kscreen-doctor is part of libkscreen and installed by default on KDE.
///
/// Output format examples:
///   Output: 1 eDP-1 enabled connected priority 1  panel
///     Modes:  0:1920x1200@165  1:1920x1200@60!
///   (where ! = preferred, no marker = available, current has no special marker in list)
///
/// kscreen-doctor output json format (--json flag) varies by version.
/// We use the text output which is more stable across versions.
/// </summary>
public class KScreenBackend : IDisplayBackend
{
    public string Name => "kscreen-doctor";
    public bool SupportsGamma => false;

    public int GetRefreshRate()
    {
        try
        {
            var info = FetchInfo();
            if (info == null)
                return -1;

            var output = FindLaptopOutput(info);
            if (output == null)
            {
                Helpers.Logger.WriteLine("KScreen.GetRefreshRate: no laptop output found");
                return -1;
            }

            var modesText = CollectModesText(info, output);
            if (modesText == null)
            {
                Helpers.Logger.WriteLine($"KScreen.GetRefreshRate: no Modes line found for {output}");
                return -1;
            }

            // Parse: "Modes:  1:1920x1200@165.00!  2:1920x1200@60.00*"
            // * = current mode, ! = preferred
            var matches = Regex.Matches(modesText, @"(\d+):(\d+x\d+)@(\d+)(?:\.\d+)?([!*]*)");
            foreach (Match m in matches)
            {
                if (m.Groups[4].Value.Contains('*'))
                {
                    if (int.TryParse(m.Groups[3].Value, out int hz))
                        return hz;
                }
            }

            Helpers.Logger.WriteLine($"KScreen.GetRefreshRate: no current mode (*) found for {output}");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("KScreen.GetRefreshRate failed", ex);
        }

        return -1;
    }

    public List<int> GetAvailableRefreshRates()
    {
        var rates = new List<int>();
        try
        {
            var info = FetchInfo();
            if (info == null)
                return rates;

            var output = FindLaptopOutput(info);
            if (output == null)
                return rates;

            var modesText = CollectModesText(info, output);
            if (modesText == null)
                return rates;

            // Find current mode's resolution (marked with *)
            string? currentRes = null;
            var matches = Regex.Matches(modesText, @"\d+:(\d+x\d+)@(\d+)(?:\.\d+)?([!*]*)");
            foreach (Match m in matches)
            {
                if (m.Groups[3].Value.Contains('*'))
                {
                    currentRes = m.Groups[1].Value;
                    break;
                }
            }
            // If no * found, use the first mode's resolution
            if (currentRes == null)
            {
                var first = Regex.Match(modesText, @"\d+:(\d+x\d+)@(\d+)");
                if (first.Success)
                    currentRes = first.Groups[1].Value;
            }

            // Collect all rates at that resolution
            foreach (Match m in matches)
            {
                string res = m.Groups[1].Value;
                if (currentRes != null && res != currentRes)
                    continue;

                if (int.TryParse(m.Groups[2].Value, out int hz) && hz > 0 && !rates.Contains(hz))
                    rates.Add(hz);
            }

            rates.Sort();
            rates.Reverse();
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("KScreen.GetAvailableRefreshRates failed", ex);
        }
        return rates;
    }

    public void SetRefreshRate(int hz)
    {
        try
        {
            Helpers.Logger.WriteLine($"KScreen.SetRefreshRate: requesting {hz}Hz");

            var info = FetchInfo();
            if (info == null)
            {
                Helpers.Logger.WriteLine("KScreen.SetRefreshRate: kscreen-doctor -o returned null");
                return;
            }

            var output = FindLaptopOutput(info);
            if (output == null)
            {
                Helpers.Logger.WriteLine("KScreen.SetRefreshRate: no laptop output found");
                return;
            }

            // Extract output index from the Output: line
            string? outputIndex = null;
            foreach (var line in info.Split('\n'))
            {
                if (line.TrimStart().StartsWith("Output:") && line.Contains(output))
                {
                    var idxMatch = Regex.Match(line, @"Output:\s+(\d+)");
                    if (idxMatch.Success)
                        outputIndex = idxMatch.Groups[1].Value;
                    break;
                }
            }

            if (outputIndex == null)
            {
                Helpers.Logger.WriteLine($"KScreen.SetRefreshRate: could not find output index for {output}");
                return;
            }

            var modesText = CollectModesText(info, output);
            if (modesText == null)
            {
                Helpers.Logger.WriteLine($"KScreen.SetRefreshRate: no Modes line found for {output}");
                return;
            }

            // Find current resolution (marked with *)
            string? currentRes = null;
            var currentMatch = Regex.Match(modesText, @"\d+:(\d+x\d+)@\d+(?:\.\d+)?[!*]*\*");
            if (currentMatch.Success)
                currentRes = currentMatch.Groups[1].Value;

            // Find mode index matching requested rate at current resolution
            string? modeIndex = null;
            var matches = Regex.Matches(modesText, @"(\d+):(\d+x\d+)@(\d+)(?:\.\d+)?");
            foreach (Match m in matches)
            {
                string res = m.Groups[2].Value;
                if (currentRes != null && res != currentRes)
                    continue;

                if (int.TryParse(m.Groups[3].Value, out int rate) && rate == hz)
                {
                    modeIndex = m.Groups[1].Value;
                    break;
                }
            }

            if (modeIndex == null)
            {
                Helpers.Logger.WriteLine($"KScreen.SetRefreshRate: mode {hz}Hz not found for {output} (currentRes={currentRes})");
                return;
            }

            var cmd = $"output.{outputIndex}.mode.{modeIndex}";
            Helpers.Logger.WriteLine($"KScreen.SetRefreshRate: running kscreen-doctor {cmd}");

            SysfsHelper.RunCommand("kscreen-doctor", cmd);
            _info = null;

            Helpers.Logger.WriteLine($"KScreen.SetRefreshRate: success - {cmd} ({hz}Hz)");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("KScreen.SetRefreshRate failed", ex);
        }
    }

    public void SetGamma(float r, float g, float b)
    {
        Helpers.Logger.WriteLine("SetGamma: not available via kscreen-doctor");
    }

    public string? GetDisplayName()
    {
        var info = FetchInfo();
        return info != null ? FindLaptopOutput(info) : null;
    }

    // Probing

    /// <summary>
    /// Test if kscreen-doctor is available and returns output info.
    /// </summary>
    public static bool Probe()
    {
        var output = SysfsHelper.RunCommand("kscreen-doctor", "-o");
        return output != null && output.Contains("Output:");
    }

    // Internal helpers

    // Callers query rate and rate list back to back; one spawn (~25 ms) serves both.
    private static string? _info;
    private static long _infoTicks;

    /// <summary>Run kscreen-doctor -o and strip ANSI codes. Returns null on failure.</summary>
    private static string? FetchInfo()
    {
        long now = Environment.TickCount64;
        if (_info != null && now - _infoTicks < 1000)
            return _info;
        var raw = SysfsHelper.RunCommand("kscreen-doctor", "-o");
        _info = raw != null ? Regex.Replace(raw, @"\x1B\[[0-9;]*m", "") : null;
        _infoTicks = now;
        return _info;
    }

    /// <summary>
    /// Collect the full modes text for a specific output, handling multi-line
    /// mode lists. Some KDE versions (Plasma 6, many modes) wrap the mode list
    /// across multiple lines. The first line starts with "Modes:", continuation
    /// lines are indented and contain mode patterns but not section keywords.
    /// Returns a single string with all modes or null if not found.
    /// </summary>
    private static string? CollectModesText(string info, string output)
    {
        bool inOutput = false;
        bool inModes = false;
        string? modesText = null;

        foreach (var line in info.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("Output:") && line.Contains(output))
            {
                inOutput = true;
                inModes = false;
                modesText = null;
                continue;
            }

            if (inOutput && trimmed.StartsWith("Output:"))
                break; // hit next output

            if (!inOutput)
                continue;

            if (trimmed.StartsWith("Modes:"))
            {
                inModes = true;
                modesText = line;
                continue;
            }

            if (inModes)
            {
                // Continuation line: still part of modes if it contains mode
                // patterns (N:WxH@R) and does NOT start with a known section.
                if (Regex.IsMatch(trimmed, @"\d+:\d+x\d+@\d+"))
                {
                    modesText += " " + trimmed;
                }
                else
                {
                    inModes = false; // hit next section
                }
            }
        }

        return modesText;
    }

    /// <summary>
    /// Find the laptop panel output name from pre-fetched info. Priority: eDP > LVDS > first.
    /// </summary>
    private static string? FindLaptopOutput(string info)
    {
        string? first = null;
        foreach (var line in info.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("Output:"))
                continue;

            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            string name = parts[2];

            if (name.StartsWith("eDP", StringComparison.OrdinalIgnoreCase))
                return name;
            if (name.StartsWith("LVDS", StringComparison.OrdinalIgnoreCase))
                first ??= name;

            first ??= name;
        }

        return first;
    }
}
