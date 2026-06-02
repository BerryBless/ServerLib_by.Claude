using System.Text;
using ServerLib.Core.Transport;

const string Host = "127.0.0.1";
const int Port = 9000;

var receivedCount = 0;
await using var connection = new SocketPipelineClient();

connection.OnConnected = () =>
{
    Console.WriteLine($"[ServerLib] Connected to {Host}:{Port}");
    return ValueTask.CompletedTask;
};

connection.OnDisconnected = () =>
{
    Console.WriteLine($"Disconnected. Total received: {receivedCount}");
    return ValueTask.CompletedTask;
};

connection.OnReceived = data =>
{
    Interlocked.Increment(ref receivedCount);
    var message = Encoding.UTF8.GetString(data.Span).TrimEnd();
    Console.WriteLine($"[Echo #{receivedCount}] recv={data.Length}B  \"{message}\"");
    return ValueTask.CompletedTask;
};

await connection.ConnectAsync(Host, Port);

// 자동 메시지 3개 전송
string[] autoMessages = ["Hello", "World", "Echo Test"];
foreach (var msg in autoMessages)
{
    var data = Encoding.UTF8.GetBytes(msg + "\n");
    await connection.SendAsync(data);
    await Task.Delay(100);
}

// 이후 대화형 모드
Console.WriteLine("\nType messages (empty line to quit):");
while (connection.IsConnected)
{
    var input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) break;

    await connection.SendAsync(Encoding.UTF8.GetBytes(input + "\n"));
}
