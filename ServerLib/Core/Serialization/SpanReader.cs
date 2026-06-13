using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ServerLib.Core.Serialization;

/// <summary>
/// <see cref="ReadOnlySpan{T}"/> 소스 버퍼에서 프리미티브를 LittleEndian으로 읽는 디코더입니다.
/// </summary>
/// <remarks>
/// <b>[Memory Allocation:]</b> Zero-allocation (ref struct, 스택 전용). 단, <see cref="ReadString"/>은
/// <see cref="string"/> 객체 생성으로 인해 1회 힙 할당이 발생합니다.
/// <b>[Thread Safety:]</b> Not Thread-safe. 단일 스레드에서만 사용해야 합니다.
/// <b>[Blocking:]</b> Non-blocking.
/// </remarks>
// ref struct: 스택 전용 타입 — 힙 캡처·박싱·필드 저장·async/람다 캡처가 컴파일 단계에서 금지된다.
// 덕분에 SpanReader 인스턴스 자체는 절대 GC 대상이 되지 않으며(Alloc 0), 디코딩이 hot path에서 무할당으로 동작한다.
public ref struct SpanReader
{
    // ReadOnlySpan은 원본 버퍼를 가리키는 얕은 참조(zero-copy 뷰)다 — 바이트를 복제하지 않고 그 위에서 직접 읽는다.
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    /// <summary>현재 읽기 위치(바이트 오프셋)입니다.</summary>
    public int Position => _position;

    /// <summary>버퍼에 남은 읽기 가능 바이트 수입니다.</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>지정한 버퍼로 SpanReader를 초기화합니다.</summary>
    /// <param name="buffer">읽기 대상 버퍼입니다.</param>
    public SpanReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>
    /// 경계 검사(A2): 남은 바이트(<see cref="Remaining"/>)보다 많이 읽으려 하면 즉시 throw합니다.
    /// </summary>
    /// <remarks>
    /// 악성·절단 패킷이 Span 인덱서/Slice의 비결정적 예외(IndexOutOfRange/ArgumentOutOfRange) 대신
    /// 의미가 명확한 <see cref="EndOfStreamException"/>을 던지게 하여, 상위 디스패치 계층(수신 루프)이
    /// "손상 패킷"으로 일관되게 격리·세션 종료하도록 한다. <c>(uint)</c> 비교로 음수 <paramref name="count"/>도
    /// 거대값으로 취급해 함께 차단한다(실패 경로에서만 예외 객체 할당, 정상 경로는 분기 1회뿐 — Zero-allocation 유지).
    /// </remarks>
    private void EnsureAvailable(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _position))
            throw new EndOfStreamException(
                "SpanReader: 버퍼에 남은 바이트보다 많이 읽으려 했습니다 (손상되었거나 잘린 패킷).");
    }

    /// <summary>1바이트 부호 없는 정수를 읽습니다.</summary>
    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    /// <summary>1바이트 bool 값을 읽습니다 (0이 아니면 true).</summary>
    public bool ReadBool()
    {
        EnsureAvailable(1);
        return _buffer[_position++] != 0;
    }

    /// <summary>2바이트 부호 있는 정수를 LittleEndian으로 읽습니다.</summary>
    public short ReadInt16()
    {
        EnsureAvailable(2);
        // BinaryPrimitives.Read*: lvalue 메모리(span)를 복사 없이 in-place로 재해석해 값(rvalue)만 추출.
        // Slice(_position) 또한 얕은복사(같은 메모리의 부분 뷰)이므로 중간 버퍼 할당이 전혀 없다.
        var value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position));
        _position += 2;
        return value;
    }

    /// <summary>2바이트 부호 없는 정수를 LittleEndian으로 읽습니다.</summary>
    public ushort ReadUInt16()
    {
        EnsureAvailable(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
        _position += 2;
        return value;
    }

    /// <summary>4바이트 부호 있는 정수를 LittleEndian으로 읽습니다.</summary>
    public int ReadInt32()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position));
        _position += 4;
        return value;
    }

    /// <summary>8바이트 부호 있는 정수를 LittleEndian으로 읽습니다.</summary>
    public long ReadInt64()
    {
        EnsureAvailable(8);
        var value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position));
        _position += 8;
        return value;
    }

    /// <summary>4바이트 단정밀도 부동소수를 LittleEndian으로 읽습니다.</summary>
    public float ReadFloat()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_position));
        _position += 4;
        return value;
    }

    /// <summary>8바이트 배정밀도 부동소수를 LittleEndian으로 읽습니다.</summary>
    public double ReadDouble()
    {
        EnsureAvailable(8);
        var value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer.Slice(_position));
        _position += 8;
        return value;
    }

    /// <summary>
    /// 지정한 길이만큼 바이트를 소스 버퍼에서 Zero-copy 슬라이스로 반환합니다.
    /// </summary>
    /// <param name="length">읽을 바이트 수입니다.</param>
    /// <returns>소스 버퍼의 슬라이스입니다. 원본 버퍼가 유효한 동안만 사용 가능합니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 복사 없이 슬라이스를 반환합니다.
    /// 반환된 <see cref="ReadOnlySpan{T}"/>는 이 SpanReader의 원본 버퍼 수명에 종속됩니다.
    /// </remarks>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        EnsureAvailable(length); // 음수·과대 length를 EndOfStreamException으로 차단(A2)
        // Slice = 얕은복사: 바이트를 복제하지 않고 원본 버퍼의 부분 뷰만 돌려준다(zero-copy).
        // 반환 스팬은 원본 _buffer 수명에 종속되므로 보관하려면 호출자가 따로 깊은복사해야 한다.
        var span = _buffer.Slice(_position, length);
        _position += length;
        return span;
    }

    /// <summary>
    /// 남은 모든 바이트를 Zero-copy 슬라이스로 반환하고 위치를 끝으로 이동합니다.
    /// 본문 전체를 길이 접두어 없이 읽어야 하는 패킷(예: <c>StatsResponsePacket</c>)에서 사용합니다.
    /// </summary>
    /// <returns>현재 위치부터 버퍼 끝까지의 슬라이스입니다. 원본 버퍼 수명에 종속됩니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 복사 없이 슬라이스를 반환합니다.
    /// 반환된 <see cref="ReadOnlySpan{T}"/>를 보관하려면 호출자가 <c>.ToArray()</c>로 깊은복사해야 합니다.
    /// </remarks>
    public ReadOnlySpan<byte> ReadRemainingBytes()
    {
        // Slice(_position): 복사 없이 현재 위치부터 끝까지의 얕은 뷰를 반환(zero-copy).
        // 이후 _position을 끝으로 이동해 중복 읽기를 방지한다.
        var span = _buffer.Slice(_position);
        _position = _buffer.Length;
        return span;
    }

    /// <summary>
    /// [길이(2B ushort) + UTF-8 바이트] 형식의 문자열을 읽습니다.
    /// </summary>
    /// <returns>디코딩된 문자열입니다.</returns>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> <see cref="string"/> 객체 생성으로 인해 1회 힙 할당이 발생합니다.
    /// hot path에서 문자열을 반복 처리하는 경우 <see cref="ReadBytes"/>로 원시 UTF-8 스팬을 받아
    /// 직접 처리하는 것을 고려하세요.
    /// </remarks>
    public string ReadString()
    {
        EnsureAvailable(2); // 길이 프리픽스 2바이트 확보 후 읽기(A2)
        ushort byteCount = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
        _position += 2;
        EnsureAvailable(byteCount); // 선언된 본문 길이가 남은 버퍼를 초과하면 차단 — 절단 패킷 방어(A2)
        // Encoding.UTF8.GetString = Alloc: UTF-8 바이트를 디코딩해 새 string 객체를 힙에 생성한다(Gen0 압력).
        // string은 불변 참조 타입이라 zero-copy가 불가능 — 이 메서드만 SpanReader에서 유일하게 할당이 발생한다.
        var value = Encoding.UTF8.GetString(_buffer.Slice(_position, byteCount));
        _position += byteCount;
        return value;
    }
}
