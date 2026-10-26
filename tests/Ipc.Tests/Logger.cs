[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
namespace GHelper.Linux.Helpers;
public static class Logger { public static void WriteLine(string message) => Console.Error.WriteLine(message); }
