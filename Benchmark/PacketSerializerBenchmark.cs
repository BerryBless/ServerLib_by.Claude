using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using ServerLib.Core.Memory;

namespace Benchmark;

[MemoryDiagnoser]
public class PacketSerializerBenchmark
{
    private byte[] _destBuffer = null!;
    private byte[] _srcBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _destBuffer = new byte[256];
        _srcBuffer = Encoding.UTF8.GetBytes("Hello, ProudNet benchmark payload!");
    }

    [Benchmark(Baseline = true, Description = "new byte[] + Encoding.GetBytes")]
    public byte[] SerializeWithNewArray()
    {
        var payload = Encoding.UTF8.GetBytes("Hello, ProudNet benchmark payload!");
        var result = new byte[PacketPool.HeaderSize + payload.Length];
        PacketPool.WriteHeader(result, packetId: 1, bodyLength: payload.Length);
        payload.CopyTo(result, PacketPool.HeaderSize);
        return result;
    }

    [Benchmark(Description = "ArrayPool.Rent + Span (Zero-Allocation)")]
    public void SerializeWithArrayPool()
    {
        var payload = _srcBuffer.AsSpan();
        var totalSize = PacketPool.HeaderSize + payload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            PacketPool.WriteHeader(rented, packetId: 1, bodyLength: payload.Length);
            payload.CopyTo(rented.AsSpan(PacketPool.HeaderSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Benchmark(Description = "HeaderParse from Span (Zero-Allocation)")]
    public bool ParseHeader()
    {
        PacketPool.WriteHeader(_destBuffer, packetId: 42, bodyLength: 100);
        return PacketPool.TryParseHeader(_destBuffer, out _, out _);
    }
}
