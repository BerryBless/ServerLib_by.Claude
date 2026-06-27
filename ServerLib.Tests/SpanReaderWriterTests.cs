using Xunit;
using ServerLib.Core.Serialization;
using System.IO;
using System.Text;

namespace ServerLib.Tests;

public class SpanReaderWriterTests
{
    // ─── Round-trip helpers ───────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_Byte()
    {
        byte[] buf = new byte[1];
        var writer = new SpanWriter(buf);
        writer.WriteByte(0xFF);

        var reader = new SpanReader(buf);
        Assert.Equal(0xFF, reader.ReadByte());
    }

    [Fact]
    public void RoundTrip_Bool_true_and_false()
    {
        byte[] buf = new byte[2];
        var writer = new SpanWriter(buf);
        writer.WriteBool(true);
        writer.WriteBool(false);

        var reader = new SpanReader(buf);
        Assert.True(reader.ReadBool());
        Assert.False(reader.ReadBool());
    }

    [Fact]
    public void RoundTrip_Int16()
    {
        byte[] buf = new byte[2];
        var writer = new SpanWriter(buf);
        writer.WriteInt16(-1234);

        var reader = new SpanReader(buf);
        Assert.Equal(-1234, reader.ReadInt16());
    }

    [Fact]
    public void RoundTrip_UInt16()
    {
        byte[] buf = new byte[2];
        var writer = new SpanWriter(buf);
        writer.WriteUInt16(60000);

        var reader = new SpanReader(buf);
        Assert.Equal(60000, reader.ReadUInt16());
    }

    [Fact]
    public void RoundTrip_Int32()
    {
        byte[] buf = new byte[4];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(int.MinValue);

        var reader = new SpanReader(buf);
        Assert.Equal(int.MinValue, reader.ReadInt32());
    }

    [Fact]
    public void RoundTrip_Int64()
    {
        byte[] buf = new byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteInt64(long.MaxValue);

        var reader = new SpanReader(buf);
        Assert.Equal(long.MaxValue, reader.ReadInt64());
    }

    [Fact]
    public void RoundTrip_Float()
    {
        byte[] buf = new byte[4];
        var writer = new SpanWriter(buf);
        writer.WriteFloat(3.14f);

        var reader = new SpanReader(buf);
        Assert.Equal(3.14f, reader.ReadFloat());
    }

    [Fact]
    public void RoundTrip_Double()
    {
        byte[] buf = new byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteDouble(Math.PI);

        var reader = new SpanReader(buf);
        Assert.Equal(Math.PI, reader.ReadDouble());
    }

    [Fact]
    public void RoundTrip_Bytes()
    {
        byte[] buf = new byte[3];
        var writer = new SpanWriter(buf);
        writer.WriteBytes(new byte[] { 1, 2, 3 });

        var reader = new SpanReader(buf);
        ReadOnlySpan<byte> result = reader.ReadBytes(3);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.ToArray());
    }

    [Fact]
    public void RoundTrip_String()
    {
        const string text = "Hello, 세계";
        int byteCount = Encoding.UTF8.GetByteCount(text);
        // 2바이트 길이 접두어 + UTF-8 본문
        byte[] buf = new byte[2 + byteCount];
        var writer = new SpanWriter(buf);
        writer.WriteString(text);

        var reader = new SpanReader(buf);
        Assert.Equal(text, reader.ReadString());
    }

    [Fact]
    public void SpanWriter_Position_and_Remaining_track_correctly()
    {
        // QUALITY-I-02: 테스트명이 양쪽 타입을 모두 커버한다는 오해를 유발하므로
        // SpanWriter 전용임을 명확히 한다. SpanReader 검증은 별도 테스트로 분리.
        const int bufSize = 16;
        byte[] buf = new byte[bufSize];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(0); // 4바이트 기록

        Assert.Equal(4, writer.Position);
        Assert.Equal(bufSize - 4, writer.Remaining);
    }

    [Fact]
    public void SpanReader_Position_and_Remaining_track_correctly()
    {
        // QUALITY-I-02: SpanReader의 Position·Remaining 프로퍼티 검증
        const int bufSize = 16;
        byte[] buf = new byte[bufSize];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(42);
        writer.WriteInt32(99);

        var reader = new SpanReader(buf);
        Assert.Equal(0, reader.Position);
        Assert.Equal(bufSize, reader.Remaining);

        reader.ReadInt32(); // 4바이트 소비
        Assert.Equal(4, reader.Position);
        Assert.Equal(bufSize - 4, reader.Remaining);
    }

    // ─── A2 Security regression: SpanReader ──────────────────────────────────
    // ref struct는 람다에 캡처할 수 없으므로, try/catch 패턴으로 예외 발생을 검증한다.

    [Fact]
    public void ReadBytes_negative_length_throws_EndOfStreamException()
    {
        // 빈 버퍼에서 음수 길이 요청 — uint 캐스트로 거대값이 되어 EnsureAvailable이 차단해야 한다
        var reader = new SpanReader(ReadOnlySpan<byte>.Empty);
        bool threw = false;
        try { reader.ReadBytes(-1); }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw, "ReadBytes(-1) 이 EndOfStreamException을 던져야 합니다.");
    }

    [Fact]
    public void ReadBytes_overread_throws_EndOfStreamException()
    {
        // 4바이트 버퍼에서 5바이트 요청 — 남은 바이트 초과
        byte[] buf = new byte[4];
        var reader = new SpanReader(buf);
        bool threw = false;
        try { reader.ReadBytes(5); }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw, "ReadBytes(5) on 4-byte buffer 이 EndOfStreamException을 던져야 합니다.");
    }

    [Fact]
    public void ReadBytes_int_max_throws_EndOfStreamException()
    {
        // 소형 버퍼에서 int.MaxValue 요청 — 반드시 차단되어야 한다
        byte[] buf = new byte[8];
        var reader = new SpanReader(buf);
        bool threw = false;
        try { reader.ReadBytes(int.MaxValue); }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw, "ReadBytes(int.MaxValue) 이 EndOfStreamException을 던져야 합니다.");
    }

    [Fact]
    public void ReadString_too_short_for_length_prefix_throws_EndOfStreamException()
    {
        // 1바이트 버퍼 — 길이 접두어(2B)도 읽을 수 없다
        byte[] buf = new byte[1];
        var reader = new SpanReader(buf);
        bool threw = false;
        try { reader.ReadString(); }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw, "ReadString() on 1-byte buffer 이 EndOfStreamException을 던져야 합니다.");
    }

    [Fact]
    public void ReadString_body_overread_throws_EndOfStreamException()
    {
        // [0x05, 0x00] = LE ushort 5 → 본문 5바이트를 요구하지만 접두어 뒤에 남은 바이트 없음
        byte[] buf = new byte[] { 0x05, 0x00 };
        var reader = new SpanReader(buf);
        bool threw = false;
        try { reader.ReadString(); }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw, "ReadString() with body overread 이 EndOfStreamException을 던져야 합니다.");
    }

    // ─── A2 Security regression: SpanWriter ──────────────────────────────────

    [Fact]
    public void WriteString_too_long_throws_ArgumentOutOfRangeException()
    {
        // 66000개의 ASCII 문자 → UTF-8 66000바이트 > 65535(ushort.MaxValue)
        string longValue = new string('A', 66000);
        // SpanWriter 버퍼는 충분히 크게 할당 (2 + 66000 = 66002)
        byte[] buf = new byte[70000];
        var writer = new SpanWriter(buf);
        bool threw = false;
        try { writer.WriteString(longValue); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "WriteString(66000자) 이 ArgumentOutOfRangeException을 던져야 합니다.");
    }
}
