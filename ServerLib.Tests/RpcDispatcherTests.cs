using Xunit;
using ServerLib.Core.Rpc;
using ServerLib.Interface;
using ServerLib.Tests.Fakes;

namespace ServerLib.Tests;

public sealed class RpcDispatcherTests
{
    // ── 1. 등록된 핸들러가 올바른 body로 호출된다 ──────────────────────────────
    [Fact]
    public async Task Dispatch_registered_handler_is_called_with_correct_body()
    {
        var dispatcher = new RpcDispatcher();
        var session = new FakeSession();

        bool called = false;
        byte[]? receivedBody = null;

        dispatcher.Register(5, (s, body, ct) =>
        {
            called = true;
            receivedBody = body.ToArray();
            return ValueTask.CompletedTask;
        });

        // payload: [0x05, 0x00] = packetId 5 (LE), [0xAA, 0xBB] = body
        var payload = new byte[] { 0x05, 0x00, 0xAA, 0xBB };
        await dispatcher.DispatchAsync(session, payload);

        Assert.True(called);
        Assert.NotNull(receivedBody);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, receivedBody);
    }

    // ── 2. 미등록 packetId는 예외 없이 무시된다 ─────────────────────────────────
    [Fact]
    public async Task Dispatch_unknown_packetId_does_not_throw()
    {
        var dispatcher = new RpcDispatcher();
        var session = new FakeSession();

        // packetId=10, 핸들러 없음
        var payload = new byte[] { 0x0A, 0x00, 0x01 };
        var ex = await Record.ExceptionAsync(() => dispatcher.DispatchAsync(session, payload).AsTask());
        Assert.Null(ex);
    }

    // ── 3. maxPacketId 경계값(A1 회귀): boundary에서 예외 없이 조용히 반환 ──────
    [Fact]
    public async Task Dispatch_packetId_at_max_boundary_does_not_throw()
    {
        var dispatcher = new RpcDispatcher(maxPacketId: 5);
        var session = new FakeSession();

        // packetId=5, _handlers 크기=5 → 배열 범위 밖 → 조용히 반환
        var payload = new byte[] { 0x05, 0x00 };
        var ex = await Record.ExceptionAsync(() => dispatcher.DispatchAsync(session, payload).AsTask());
        Assert.Null(ex);
    }

    // ── 4. payload가 2바이트 미만(1바이트)이면 조용히 반환 ──────────────────────
    [Fact]
    public async Task Dispatch_payload_shorter_than_2_bytes_does_not_throw()
    {
        var dispatcher = new RpcDispatcher();
        var session = new FakeSession();

        var payload = new byte[] { 0x01 };
        var ex = await Record.ExceptionAsync(() => dispatcher.DispatchAsync(session, payload).AsTask());
        Assert.Null(ex);
    }

    // ── 5. 빈 payload(0바이트)는 조용히 반환 ────────────────────────────────────
    [Fact]
    public async Task Dispatch_empty_payload_does_not_throw()
    {
        var dispatcher = new RpcDispatcher();
        var session = new FakeSession();

        var payload = Array.Empty<byte>();
        var ex = await Record.ExceptionAsync(() => dispatcher.DispatchAsync(session, payload).AsTask());
        Assert.Null(ex);
    }

    // ── 6. Register: maxPacketId 경계값(A1 회귀) → ArgumentOutOfRangeException ──
    [Fact]
    public void Register_packetId_at_max_throws_ArgumentOutOfRangeException()
    {
        var dispatcher = new RpcDispatcher(maxPacketId: 5);

        // 유효 ID: 0~4. ID=5는 배열 범위 밖 → ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            dispatcher.Register(5, (_, _, _) => ValueTask.CompletedTask));
    }

    // ── 7. Register: 유효 범위 내 ID는 예외 없음 ────────────────────────────────
    [Fact]
    public void Register_packetId_in_range_succeeds()
    {
        var dispatcher = new RpcDispatcher(maxPacketId: 5);

        // ID=4는 유효(0~4)
        var ex = Record.Exception(() =>
            dispatcher.Register(4, (_, _, _) => ValueTask.CompletedTask));
        Assert.Null(ex);
    }

    // ── 8. 핸들러가 받는 body에 packetId 2바이트가 포함되지 않는다 ───────────────
    [Fact]
    public async Task Handler_receives_body_without_packetId_prefix()
    {
        var dispatcher = new RpcDispatcher();
        var session = new FakeSession();

        byte[]? receivedBody = null;
        dispatcher.Register(3, (_, body, _) =>
        {
            receivedBody = body.ToArray();
            return ValueTask.CompletedTask;
        });

        // packetId=3 (LE: 0x03, 0x00), body=[0xDE, 0xAD]
        var payload = new byte[] { 0x03, 0x00, 0xDE, 0xAD };
        await dispatcher.DispatchAsync(session, payload);

        Assert.NotNull(receivedBody);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, receivedBody);
    }

    // ── 9. null 핸들러 슬롯(미등록 ID)은 예외 없이 무시된다 ─────────────────────
    [Fact]
    public async Task Null_handler_slot_does_not_throw()
    {
        var dispatcher = new RpcDispatcher();
        var session = new FakeSession();

        // ID=1만 등록, ID=2는 null 슬롯
        dispatcher.Register(1, (_, _, _) => ValueTask.CompletedTask);

        var payload = new byte[] { 0x02, 0x00, 0xFF };
        var ex = await Record.ExceptionAsync(() => dispatcher.DispatchAsync(session, payload).AsTask());
        Assert.Null(ex);
    }
}
