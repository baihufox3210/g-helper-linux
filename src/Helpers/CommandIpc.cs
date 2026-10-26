using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using GHelper.Linux.Ipc;

namespace GHelper.Linux.Helpers;

/// <summary>Bounded, user-private newline-delimited UTF-8 transport.</summary>
public static class CommandIpc
{
    public const int MaxRequestBytes = 8192;
    private static Socket? _server;
    private static CancellationTokenSource? _stop;
    private static string? _boundPath;
    private static readonly SemaphoreSlim Clients = new(8, 8);
    private static string SocketPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? throw new IOException("XDG_RUNTIME_DIR is required"), "ghelper.sock");

    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int getsockopt(int fd, int level, int option, byte[] value, ref uint length);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, string path, int flags, uint mask, byte[] buffer);
    private static bool IsCurrentUser(Socket socket)
    {
        // Linux SO_PEERCRED returns struct ucred { pid_t pid; uid_t uid; gid_t gid; }.
        var cred = new byte[12];
        uint length = 12;
        return getsockopt(socket.Handle.ToInt32(), 1, 17, cred, ref length) == 0
            && length == 12 && BitConverter.ToUInt32(cred, 4) == geteuid();
    }

    public static bool TrySend(string command)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                { SendTimeout = 2000, ReceiveTimeout = 2000 };
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            if (!IsCurrentUser(client)) return false;
            using var stream = new NetworkStream(client);
            stream.Write(Encoding.UTF8.GetBytes(command + "\n"));
            return true;
        }
        catch { return false; }
    }

    public static void StartServer(Func<string, Task<string>> onCommand)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            string path = SocketPath;
            string directory = Path.GetDirectoryName(path)!;
            // Fail closed instead of publishing a control socket in shared /tmp.
            // Linux statx UAPI: 256-byte struct, uid at byte 20, stx_mode at byte 28.
            // AT_FDCWD=-100, AT_SYMLINK_NOFOLLOW=0x100, STATX_BASIC_STATS=0x7ff.
            var stat = new byte[256];
            if (statx(-100, directory, 0x100, 0x7ff, stat) != 0
                || BitConverter.ToUInt32(stat, 20) != geteuid()
                || (BitConverter.ToUInt16(stat, 28) & 0xf000) != 0x4000)
                throw new IOException("Runtime directory must be owned by the current user and not be a symlink");
            var mode = File.GetUnixFileMode(directory);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new IOException("Runtime directory must be user-private (0700)");
            if (File.Exists(path)) File.Delete(path); // Single-instance lock is already held.
            var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                server.Bind(new UnixDomainSocketEndPoint(path));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                server.Listen(8);
            }
            catch { server.Dispose(); throw; }
            _boundPath = path;
            _server = server;
            _stop = new CancellationTokenSource();
            _ = AcceptAsync(server, onCommand, _stop.Token);
        }
        catch (Exception ex) { Logger.WriteLine($"CommandIpc: bind failed: {ex.Message}"); }
    }

    private static async Task AcceptAsync(Socket server, Func<string, Task<string>> handler, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await server.AcceptAsync(stop);
                if (!IsCurrentUser(client) || !Clients.Wait(0)) { client.Dispose(); continue; }
                _ = ServeAsync(client, handler, stop);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        { if (!stop.IsCancellationRequested) Logger.WriteLine($"CommandIpc: accept failed: {ex.Message}"); }
    }

    private static async Task ServeAsync(Socket client, Func<string, Task<string>> handler, CancellationToken stop)
    {
        try
        {
            using (client)
            using (var stream = new NetworkStream(client))
            {
                var buffer = new byte[MaxRequestBytes + 1];
                // One frame per connection; no buffering ambiguity or unbounded sessions.
                int count = 0;
                string? response = null;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    while (count <= MaxRequestBytes)
                    {
                        int n = await stream.ReadAsync(buffer.AsMemory(count, 1), readTimeout.Token);
                        if (n == 0) return;
                        if (buffer[count++] == (byte)'\n') break;
                    }
                }
                if (count > MaxRequestBytes)
                    response = IpcCommandRouter.Error(null, "request_too_large", "Request exceeds 8192 bytes including newline");
                else
                {
                    try
                    {
                        string line = new UTF8Encoding(false, true).GetString(buffer, 0, count - 1).TrimEnd('\r');
                        // Hardware work is off the UI thread. It is deliberately NOT cancelled:
                        // abandoning an in-flight write would allow overlapping operations.
                        response = await handler(line);
                    }
                    catch (DecoderFallbackException) { response = IpcCommandRouter.Error(null, "invalid_request", "Invalid UTF-8"); }
                }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), writeTimeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { /* Disconnected or expired client must not terminate the listener. */ }
        finally { Clients.Release(); }
    }

    public static void Stop()
    {
        _stop?.Cancel();
        _server?.Dispose();
        _server = null;
        try { if (_boundPath != null) File.Delete(_boundPath); } catch { }
        _boundPath = null;
    }
}
