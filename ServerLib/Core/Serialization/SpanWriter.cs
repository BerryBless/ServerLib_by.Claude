using System.Buffers.Binary;
using System.Text;

namespace ServerLib.Core.Serialization;

/// <summary>
/// <see cref="Span{T}"/> 목적지 버퍼에 프리미티브를 LittleEndian으로 직접 기록하는 인코더입니다.
/// </summary>
/// <remarks>
/// <b>[Memory Allocation:]</b> Zero-allocation. ref struct이므로 스택에만 존재하며 힙 할당이 없습니다.
/// <b>[Thread Safety:]</b> Not Thread-safe. 단일 스레드에서만 사용해야 합니다.
/// <b>[Blocking:]</b> Non-blocking. 모든 연산이 동기 즉시 반환됩니다.
/// </remarks>
// ref struct: 스택 전용 — 힙 캡처·박싱·필드 저장이 금지되어 인코더 자체가 GC 대상이 되지 않는다(Alloc 0).
public ref struct SpanWriter
{
    // Span은 호출자가 미리 확보한 목적지 버퍼를 가리키는 얕은 참조(lvalue 뷰) — 내부에서 새 버퍼를 만들지 않고 그 위에 직접 기록.
    private readonly Span<byte> _buffer;
    private int _position;

    /// <summary>현재 쓰기 위치(바이트 오프셋)입니다.</summary>
    public int Position => _position;

    /// <summary>버퍼에 남은 쓰기 가능 용량(바이트)입니다.</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>지정한 버퍼로 SpanWriter를 초기화합니다.</summary>
    /// <param name="buffer">기록 대상 버퍼입니다. 충분한 크기를 사전에 확보해야 합니다.</param>
    public SpanWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>1바이트 부호 없는 정수를 기록합니다.</summary>
    public void WriteByte(byte value) =>
        _buffer[_position++] = value;

    /// <summary>bool 값을 1바이트(0 또는 1)로 기록합니다.</summary>
    public void WriteBool(bool value) =>
        _buffer[_position++] = value ? (byte)1 : (byte)0;

    /// <summary>2바이트 부호 있는 정수를 LittleEndian으로 기록합니다.</summary>
    public void WriteInt16(short value)
    {
        // BinaryPrimitives.Write*: rvalue(value)를 lvalue 메모리(span)에 in-place로 직접 기록 — 박싱·임시 버퍼 없음.
        // Slice는 얕은복사(부분 뷰)이므로 기록 위치만 가리킬 뿐 복제가 일어나지 않는다.
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_position), value);
        _position += 2;
    }

    /// <summary>2바이트 부호 없는 정수를 LittleEndian으로 기록합니다.</summary>
    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position), value);
        _position += 2;
    }

    /// <summary>4바이트 부호 있는 정수를 LittleEndian으로 기록합니다.</summary>
    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position), value);
        _position += 4;
    }

    /// <summary>8바이트 부호 있는 정수를 LittleEndian으로 기록합니다.</summary>
    public void WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position), value);
        _position += 8;
    }

    /// <summary>4바이트 단정밀도 부동소수를 LittleEndian으로 기록합니다.</summary>
    public void WriteFloat(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.Slice(_position), value);
        _position += 4;
    }

    /// <summary>8바이트 배정밀도 부동소수를 LittleEndian으로 기록합니다.</summary>
    public void WriteDouble(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.Slice(_position), value);
        _position += 8;
    }

    /// <summary>
    /// 바이트 배열을 길이 접두어 없이 버퍼에 직접 복사합니다.
    /// </summary>
    /// <param name="value">복사할 바이트 데이터입니다.</param>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation. 버퍼 내부에서 직접 복사합니다.
    /// </remarks>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        // CopyTo = 깊은복사: 원본 바이트를 목적지 버퍼로 실제 복제한다.
        // 직렬화 결과는 호출자 버퍼가 독립적으로 소유해야 하므로, 슬라이스(얕은복사)가 아니라 내용 복제가 필요하다.
        value.CopyTo(_buffer.Slice(_position));
        _position += value.Length;
    }

    /// <summary>
    /// 문자열을 [길이(2B ushort) + UTF-8 바이트] 형식으로 기록합니다.
    /// 최대 문자열 바이트 길이: 65535.
    /// </summary>
    /// <param name="value">기록할 문자열입니다.</param>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> UTF-8 인코딩 과정에서 임시 버퍼 할당 없이 목적지에 직접 기록합니다.
    /// </remarks>
    public void WriteString(string value)
    {
        // GetByteCount: 실제 인코딩 전에 UTF-8 바이트 수를 먼저 스캔(문자열 1회 순회). 길이 접두어 기록과 범위 검증에 필요.
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), byteCount, "UTF-8 인코딩 바이트 길이가 65535를 초과합니다.");
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position), (ushort)byteCount);
        _position += 2;
        // GetBytes(string, span): 임시 byte[] 할당 없이 목적지 span에 직접 인코딩(Alloc 0). 단 GetByteCount+GetBytes로 문자열을 2회 순회한다.
        Encoding.UTF8.GetBytes(value, _buffer.Slice(_position, byteCount));
        _position += byteCount;
    }

    /// <summary>
    /// 사전에 계산된 UTF-8 바이트 수를 사용하여 문자열을 기록합니다.
    /// <see cref="WriteString(string)"/> 대비 <c>GetByteCount</c> 호출 1회 절감.
    /// </summary>
    /// <param name="value">기록할 문자열입니다.</param>
    /// <param name="precomputedByteCount">호출자가 미리 계산한 UTF-8 바이트 수입니다.</param>
    /// <remarks>
    /// <b>[Memory Allocation:]</b> Zero-allocation.
    /// </remarks>
    public void WriteString(string value, int precomputedByteCount)
    {
        if ((uint)precomputedByteCount > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(precomputedByteCount), precomputedByteCount, "UTF-8 인코딩 바이트 길이가 65535를 초과합니다.");
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position), (ushort)precomputedByteCount);
        _position += 2;
        // precomputedByteCount를 받아 GetByteCount 스캔을 생략 → 문자열을 1회만 순회(GetBodySize 단계에서 이미 계산해 캐시한 값 재사용).
        Encoding.UTF8.GetBytes(value, _buffer.Slice(_position, precomputedByteCount));
        _position += precomputedByteCount;
    }
}
