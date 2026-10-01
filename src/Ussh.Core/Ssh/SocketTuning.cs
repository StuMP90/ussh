using System.Net.Sockets;
using System.Reflection;
using Renci.SshNet;
using Ussh.Core.Diagnostics;

namespace Ussh.Core.Ssh;

/// <summary>
/// Sets OS-level dead-peer detection on SSH.NET's underlying socket.
///
/// SSH keepalives alone keep NAT mappings open, but if the network silently vanishes the
/// keepalive bytes just sit in the send queue and the OS retransmits for ~15 minutes
/// before failing the socket, so the tab looks frozen. Capping retransmission time makes
/// the socket fail within <c>timeout</c>, which triggers our reconnect logic promptly.
///
/// SSH.NET does not expose its socket, so this uses reflection. It is best-effort: if a
/// future SSH.NET release renames the fields, it logs once and the session still works,
/// just with slower detection of dead links.
/// </summary>
internal static class SocketTuning
{
    private const int LinuxTcpUserTimeout = 18; // TCP_USER_TIMEOUT
    private const int WindowsTcpMaxRetransmitTime = 5; // TCP_MAXRT
    private static int _warned;

    public static void Apply(BaseClient client, TimeSpan timeout)
    {
        try
        {
            var socket = FindSocket(client);
            if (socket == null)
            {
                WarnOnce("Could not locate SSH.NET socket; OS dead-peer timeout not applied.");
                return;
            }

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var seconds = (int)Math.Max(10, timeout.TotalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, seconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

            if (OperatingSystem.IsLinux())
                socket.SetRawSocketOption((int)SocketOptionLevel.Tcp, LinuxTcpUserTimeout, BitConverter.GetBytes(seconds * 1000));
            else if (OperatingSystem.IsWindows())
                socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)WindowsTcpMaxRetransmitTime, seconds);
        }
        catch (Exception ex)
        {
            WarnOnce("Failed to apply socket options: " + ex.Message);
        }
    }

    private static Socket? FindSocket(BaseClient client)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var session = typeof(BaseClient).GetProperty("Session", flags)?.GetValue(client);
        if (session == null)
            return null;
        for (var type = session.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetFields(flags).FirstOrDefault(f => typeof(Socket).IsAssignableFrom(f.FieldType));
            if (field != null)
                return field.GetValue(session) as Socket;
        }
        return null;
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            Log.Warn(nameof(SocketTuning), message);
    }
}
