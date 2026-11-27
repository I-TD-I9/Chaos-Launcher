using System.Net.Sockets;

namespace Chaos;

/// <summary>Checks that the game server is reachable.</summary>
public static class ServerProbe
{
    public static async Task<bool> CanConnectAsync(ServerEndpoints server, TimeSpan timeout,
                                                  CancellationToken cancel = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(server.GateHost, server.GatePort, limit.Token);
            return true;
        }
        catch (Exception e) when ((e is SocketException or OperationCanceledException) && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }
}
