using System.Net.Sockets;
using System.Text;

const string Host = "127.0.0.1";
const int Port = 9000;

using var client = new TcpClient();
await client.ConnectAsync(Host, Port);
Console.WriteLine($"Connected to {Host}:{Port}. Type messages (empty line to quit):");

await using var stream = client.GetStream();
var receiveBuffer = new byte[4096];

while (true)
{
    var input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) break;

    var data = Encoding.UTF8.GetBytes(input + "\n");
    await stream.WriteAsync(data);

    int bytesRead = await stream.ReadAsync(receiveBuffer);
    var response = Encoding.UTF8.GetString(receiveBuffer, 0, bytesRead);
    Console.WriteLine($"Echo: {response.TrimEnd()}");
}
