using System.Buffers;
using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using ServerLib.Core.Transport;

namespace Benchmark;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class SessionSendBenchmark
{
    private SocketPipelineSession _session = null!;
    private Socket _serverSocket = null!;
    private Socket _clientSocket = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _clientSocket.Connect(IPAddress.Loopback, port);
        _serverSocket = listener.Accept();
        listener.Dispose();

        _session = new SocketPipelineSession(_serverSocket);
        _payload = ArrayPool<byte>.Shared.Rent(128);
        new Random(42).NextBytes(_payload);
    }

    [Benchmark(Description = "SendAsync 128B (ArrayPool buffer)")]
    public async ValueTask SendAsync128Bytes()
    {
        await _session.SendAsync(_payload.AsMemory(0, 128));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _session.DisposeAsync();
        _clientSocket.Dispose();
        ArrayPool<byte>.Shared.Return(_payload);
    }
}
