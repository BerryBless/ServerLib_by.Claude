using System.Net;
using System.Net.Sockets;
using System.Text;

const int Port = 9000;

var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"Echo server listening on port {Port}...");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = HandleClientAsync(client);
}

static async Task HandleClientAsync(TcpClient client)
{
    var endpoint = client.Client.RemoteEndPoint;
    Console.WriteLine($"[+] Connected: {endpoint}");

    await using var stream = client.GetStream();
    var buffer = new byte[4096];

    try
    {
        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) break;

            var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            Console.WriteLine($"[{endpoint}] Received: {message.TrimEnd()}");

            await stream.WriteAsync(buffer.AsMemory(0, bytesRead));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{endpoint}] Error: {ex.Message}");
    }
    finally
    {
        client.Close();
        Console.WriteLine($"[-] Disconnected: {endpoint}");
    }
}
