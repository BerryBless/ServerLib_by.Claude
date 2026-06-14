using System.Buffers;
using System.Text;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace ServerLib.Examples.Examples;

/// <summary>
/// ServerLib의 직렬화 빌딩블록 전체를 인프로세스(소켓 불필요)로 시연합니다.
/// <see cref="SpanWriter"/>/<see cref="SpanReader"/>의 모든 Write*/Read* 메서드,
/// <see cref="BinaryPacketSerializer"/>의 전체 API,
/// <see cref="PacketPool"/>의 직접 사용 패턴을 다룹니다.
/// </summary>
/// <remarks>
/// <b>[시연 API]</b>
/// <list type="bullet">
/// <item><description><see cref="SpanWriter"/>: WriteByte/Bool/Int16/UInt16/Int32/Int64/Float/Double/Bytes/String + WriteString(value,precount) + Position/Remaining</description></item>
/// <item><description><see cref="SpanReader"/>: ReadByte/Bool/Int16/UInt16/Int32/Int64/Float/Double/Bytes/String + Position/Remaining</description></item>
/// <item><description><see cref="BinaryPacketSerializer.Serialize{T}"/> / <see cref="BinaryPacketSerializer.Deserialize{T}"/> / <see cref="BinaryPacketSerializer.TryReadPacketLength"/></description></item>
/// <item><description><see cref="PacketPool.HeaderSize"/> / <see cref="PacketPool.WriteHeader"/> / <see cref="PacketPool.TryParseHeader"/> / <see cref="PacketPool.RentSendBuffer"/> / <see cref="PacketPool.ReturnSendBuffer"/></description></item>
/// </list>
/// </remarks>
internal static class Serialization
{
    /// <summary>
    /// SpanWriter/SpanReader의 전 Write*/Read* API를 왕복 검증하고
    /// BinaryPacketSerializer·PacketPool의 직접 사용 패턴을 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> SpanWriter/SpanReader는 ref struct이므로 스택 전용 — 멀티스레드 공유 불가(컴파일러가 방지).
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b> SpanWriter/SpanReader는 ref struct(스택) — Zero-allocation. 테스트 버퍼만 한 번 할당합니다.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking. 전 연산이 동기 즉시 반환입니다.
    /// </remarks>
    public static Task RunAsync()
    {
        DemoSpanWriterReader();
        DemoBinaryPacketSerializer();
        DemoPacketPoolDirect();
        Console.WriteLine("[OK] 04_Serialization");
        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="SpanWriter"/>/<see cref="SpanReader"/>의 모든 기본 타입과 Position/Remaining을 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> ref struct — 스택 전용, 멀티스레드 공유 불가(컴파일러 보장).
    /// <b>[Memory Allocation:]</b> Zero-allocation (ref struct는 힙 할당 없음).
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    private static void DemoSpanWriterReader()
    {
        Console.WriteLine("  [SpanWriter/SpanReader] 전 타입 왕복 시연:");

        // 충분히 큰 버퍼를 스택에 할당 — SpanWriter는 이 Span을 직접 씁니다(힙 할당 없음).
        Span<byte> buffer = stackalloc byte[256];
        var writer = new SpanWriter(buffer);

        // ── 기본 타입 쓰기 ──
        // WriteByte: 1바이트
        writer.WriteByte(0xAB);
        // WriteBool: 1바이트 (true=1, false=0)
        writer.WriteBool(true);
        writer.WriteBool(false);
        // WriteInt16/WriteUInt16: 2바이트 LE
        writer.WriteInt16(-1234);
        writer.WriteUInt16(65000);
        // WriteInt32: 4바이트 LE
        writer.WriteInt32(-99999);
        // WriteInt64: 8바이트 LE
        writer.WriteInt64(long.MaxValue);
        // WriteFloat: 4바이트 IEEE 754
        writer.WriteFloat(3.14f);
        // WriteDouble: 8바이트 IEEE 754
        writer.WriteDouble(2.718281828);
        // WriteBytes: 원시 바이트 배열
        byte[] rawBytes = [0x01, 0x02, 0x03];
        writer.WriteBytes(rawBytes);
        // WriteString: ushort 길이(2B) + UTF-8 바이트
        writer.WriteString("안녕, 세계!");
        // WriteString(value, precomputedByteCount): UTF-8 바이트 수를 미리 계산해 이중 스캔 방지
        string preStr = "최적화 문자열";
        int preByteCount = Encoding.UTF8.GetByteCount(preStr);
        writer.WriteString(preStr, preByteCount); // GetBodySize/Serialize 이중 스캔 패턴

        // Position: 현재까지 쓴 바이트 수
        // Remaining: 버퍼 여유 공간
        Console.WriteLine($"    Writer.Position={writer.Position}, Writer.Remaining={writer.Remaining}");

        // ── 같은 데이터를 SpanReader로 읽기 ──
        var reader = new SpanReader(buffer[..writer.Position]);

        byte b       = reader.ReadByte();
        bool t       = reader.ReadBool();
        bool f       = reader.ReadBool();
        short i16    = reader.ReadInt16();
        ushort u16   = reader.ReadUInt16();
        int i32      = reader.ReadInt32();
        long i64     = reader.ReadInt64();
        float fl     = reader.ReadFloat();
        double db    = reader.ReadDouble();
        // ReadBytes(n): n바이트 슬라이스를 반환 — 수신 버퍼의 얕은 뷰(zero-copy).
        // 콜백 반환 후 보관하려면 ToArray()로 깊은복사 필요.
        ReadOnlySpan<byte> rb = reader.ReadBytes(3);
        // ReadString: ushort 길이를 읽은 뒤 UTF-8 decode — string은 불변이라 새 인스턴스 할당(Alloc).
        string str1  = reader.ReadString();
        string str2  = reader.ReadString();

        Console.WriteLine($"    읽기 검증: byte=0x{b:X2} bool={t},{f} i16={i16} u16={u16} i32={i32} i64={i64}");
        Console.WriteLine($"    float={fl:F2} double={db:F9} bytes=[{string.Join(",", rb.ToArray().Select(x => $"0x{x:X2}"))}]");
        Console.WriteLine($"    str1=\"{str1}\" str2=\"{str2}\"");
        Console.WriteLine($"    Reader.Position={reader.Position}, Reader.Remaining={reader.Remaining}");

        // 기댓값 검증
        if (b != 0xAB || t != true || f != false || i16 != -1234 || u16 != 65000)
            throw new InvalidOperationException("SpanWriter/SpanReader 기본 타입 검증 실패");
        if (i32 != -99999 || i64 != long.MaxValue || Math.Abs(fl - 3.14f) > 1e-5f)
            throw new InvalidOperationException("SpanWriter/SpanReader 수치 타입 검증 실패");
        if (str1 != "안녕, 세계!" || str2 != preStr)
            throw new InvalidOperationException("SpanWriter/SpanReader 문자열 검증 실패");
    }

    /// <summary>
    /// <see cref="BinaryPacketSerializer"/>의 Serialize/Deserialize/TryReadPacketLength를 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe(내부 상태 없음 — 모든 작업이 파라미터에만 의존).
    /// <b>[Memory Allocation:]</b> Serialize: Zero-allocation(기존 Span에 씀). Deserialize<sealed class>: 1회 힙 할당.
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    private static void DemoBinaryPacketSerializer()
    {
        Console.WriteLine("  [BinaryPacketSerializer] Serialize/Deserialize/TryReadPacketLength:");

        var serializer = new BinaryPacketSerializer();
        var pkt = new EchoPacket { Message = "직렬화 테스트" };

        // ── Serialize<T>: 4바이트 헤더 + 본문을 Span<byte>에 기록 ──
        int pktSize = PacketPool.HeaderSize + pkt.GetBodySize();
        Span<byte> span = stackalloc byte[pktSize];
        int written = serializer.Serialize(pkt, span);
        Console.WriteLine($"    Serialize(): {written}바이트 기록 (헤더{PacketPool.HeaderSize}B + 본문{pkt.GetBodySize()}B)");

        // ── TryReadPacketLength: 헤더 4바이트만 보고 전체 패킷 길이를 예측 ──
        // 수신 파이프에서 버퍼에 충분한 데이터가 쌓였는지 확인할 때 사용합니다.
        bool ok = serializer.TryReadPacketLength(span[..PacketPool.HeaderSize], out int totalLen);
        Console.WriteLine($"    TryReadPacketLength(헤더4B): ok={ok}, totalLength={totalLen} (예상={pktSize})");
        if (!ok || totalLen != pktSize)
            throw new InvalidOperationException("TryReadPacketLength 검증 실패");

        // ── Deserialize<T>: Span에서 패킷 복원 ──
        // EchoPacket은 sealed class → new EchoPacket() 1회 힙 할당 + ReadString 1회.
        var decoded = serializer.Deserialize<EchoPacket>(span);
        Console.WriteLine($"    Deserialize<EchoPacket>(): Message=\"{decoded.Message}\"");
        if (decoded.Message != pkt.Message)
            throw new InvalidOperationException("BinaryPacketSerializer 왕복 검증 실패");
    }

    /// <summary>
    /// <see cref="PacketPool"/>의 정적 멤버를 직접 사용하는 패턴을 시연합니다.
    /// </summary>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe. PacketPool의 모든 정적 메서드는 스레드 안전합니다.
    /// <br/><br/>
    /// <b>[Memory Allocation:]</b>
    /// <see cref="PacketPool.RentSendBuffer"/> / <see cref="PacketPool.ReturnSendBuffer"/>는
    /// <see cref="ArrayPool{T}.Shared"/> 위에 구축된 래퍼 — 반납 보장 필수.
    /// <br/><br/>
    /// <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    private static void DemoPacketPoolDirect()
    {
        Console.WriteLine("  [PacketPool] 직접 사용 패턴:");
        Console.WriteLine($"    PacketPool.HeaderSize = {PacketPool.HeaderSize}");

        // ── PacketPool.RentSendBuffer / ReturnSendBuffer ──
        // RentSendBuffer: ArrayPool<byte>.Shared.Rent의 래퍼 — 빌딩블록을 직접 제어할 때 사용.
        // 반드시 ReturnSendBuffer로 반납해야 합니다(PacketSendExtensions는 자동 반납).
        int needed = PacketPool.HeaderSize + 4; // 헤더 + int 1개
        byte[] rentedBuf = PacketPool.RentSendBuffer(needed);
        try
        {
            // ── PacketPool.WriteHeader: 헤더를 수동으로 기록 ──
            // 4바이트 헤더를 직접 구성할 때 사용합니다. [packetId(2B LE) | bodyLen(2B LE)]
            ushort packetId = 42;
            ushort bodyLen  = 4;
            PacketPool.WriteHeader(rentedBuf, packetId, bodyLen);
            // 본문 영역에 직접 데이터 쓰기 (예시: int 0x12345678)
            var bodySpan = rentedBuf.AsSpan(PacketPool.HeaderSize, bodyLen);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bodySpan, 0x12345678);

            Console.WriteLine($"    WriteHeader(id={packetId}, bodyLen={bodyLen}) → 헤더 {PacketPool.HeaderSize}바이트 기록");

            // ── PacketPool.TryParseHeader: 헤더 파싱 ──
            if (PacketPool.TryParseHeader(rentedBuf.AsSpan(0, needed), out ushort parsedId, out int parsedBodyLen))
            {
                Console.WriteLine($"    TryParseHeader(): id={parsedId}, bodyLen={parsedBodyLen}");
                if (parsedId != packetId || parsedBodyLen != bodyLen)
                    throw new InvalidOperationException("PacketPool 헤더 왕복 검증 실패");
            }
            else
            {
                throw new InvalidOperationException("PacketPool.TryParseHeader 실패");
            }
        }
        finally
        {
            // 반납 누락 시 ArrayPool 고갈 → new byte[] 폴백(GC 압력 누적)
            PacketPool.ReturnSendBuffer(rentedBuf);
            Console.WriteLine("    ReturnSendBuffer() 완료");
        }
    }
}
