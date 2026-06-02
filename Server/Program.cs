using System.Text;
using ServerLib.Core;
using ServerLib.Core.Transport;
using ServerLib.Interface;

const int Port = 9000;

var metrics = new ServerMetrics();
var listener = new SocketPipelineListener();

listener.OnClientConnected = session =>
{
    metrics.OnClientConnected();
    Console.WriteLine($"[+] {session.RemoteEndPoint}  (total: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};

listener.OnClientDisconnected = session =>
{
    metrics.OnClientDisconnected();
    Console.WriteLine($"[-] {session.RemoteEndPoint}  (total: {metrics.ConnectedCount})");
    return ValueTask.CompletedTask;
};

listener.OnReceived = async (session, data) =>
{
    metrics.OnPacketReceived();
    metrics.OnBytesReceived(data.Length);

    var message = Encoding.UTF8.GetString(data.Span).TrimEnd();
    Console.WriteLine($"[{session.RemoteEndPoint}] recv={data.Length}B  \"{message}\"");

    await session.SendAsync(data);
    metrics.OnBytesSent(data.Length);
};

listener.Start(Port);
Console.WriteLine($"[ServerLib] SocketPipelineListener on port {Port}. Press Enter to stop.");
Console.ReadLine();

listener.Stop();
Console.WriteLine($"Server stopped. Total packets: {metrics.TotalPacketsReceived}");
