using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Addressing;
using CanKit.Pro.J1939;
using CanKit.Pro.J1939Tp;
using FluentAssertions;
using Xunit;

namespace CanKit.Tests.TestCases.J1939;

/// <summary>
/// Virtual-loopback integration tests for the SAE J1939 application-layer node
/// (<c>CanKit.Pro.J1939</c>), covering SRS FR-J1939-001..006 Must and FR-J1939-007 Should.
/// </summary>
public class J1939NodeTests : IClassFixture<TestCaseProvider>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static string NewSession() => $"j1939-{Guid.NewGuid():N}";

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(TestCaseProvider.AbitRate));

    /// <summary>Constructs a NAME parameterized by a caller-controlled identity number so
    /// tests can force a deterministic winner in a claim conflict (numerically-lower NAME
    /// wins per SAE J1939-81 §4.4.3.2).</summary>
    private static J1939Name Name(uint identity, ushort manufacturerCode = 0x100) =>
        new J1939Name(
            identityNumber: identity,
            manufacturerCode: manufacturerCode,
            ecuInstance: 0,
            functionInstance: 0,
            function: 0x81,
            reserved: false,
            vehicleSystem: 0,
            vehicleSystemInstance: 0,
            industryGroup: 0,
            arbitraryAddressCapable: false);

    private static async Task<J1939Message> WaitForMessageAsync(
        IJ1939Node node,
        Func<J1939Message, bool> predicate,
        TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<J1939Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<J1939Message> handler = (_, msg) =>
        {
            if (predicate(msg)) tcs.TrySetResult(msg);
        };
        node.MessageReceived += handler;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            node.MessageReceived -= handler;
        }
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-001: PGN send/receive with 29-bit Priority/PF/PS/SA encode/decode.
    // ---------------------------------------------------------------------------------------

    // PDU2 (broadcast) PGN round-trip. Payload arrives on the receiver with the correct PGN,
    // priority and source address decoded back out of the 29-bit ID.
    [Fact]
    public async Task Pdu2_SingleFrameRoundtrip_DecodesPgnPriorityAndSa()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var sender = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        using var receiver = J1939Node.Open(busB, new J1939NodeOptions(Name(2)));

        await sender.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);
        await receiver.ClaimAddressAsync(0x22).WithTimeout(ShortTimeout);

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var message = new J1939Message(pgn: 0xFEF1u, payload: payload, priority: 5,
            destinationAddress: 0xFF); // PDU2, DA ignored

        var receiveTask = WaitForMessageAsync(receiver, m => m.Pgn == 0xFEF1u, ShortTimeout);
        await sender.SendAsync(message).WithTimeout(ShortTimeout);

        var received = await receiveTask;
        received.Pgn.Should().Be(0xFEF1u);
        received.Priority.Should().Be(5);
        received.SourceAddress.Should().Be(0x11);
        received.DestinationAddress.Should().Be(0xFF);
        received.Payload.ToArray().Should().Equal(payload);
        received.WasMultiFrame.Should().BeFalse();
    }

    // PDU1 (peer-to-peer) PGN round-trip. Destination address is preserved and the PGN group
    // extension is stripped correctly (PDU1 PS is a destination, not part of the PGN).
    [Fact]
    public async Task Pdu1_SingleFrameRoundtrip_DecodesDestinationAddress()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var sender = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        using var receiver = J1939Node.Open(busB, new J1939NodeOptions(Name(2)));

        await sender.ClaimAddressAsync(0x33).WithTimeout(ShortTimeout);
        await receiver.ClaimAddressAsync(0x44).WithTimeout(ShortTimeout);

        // 0xEF00 is a PDU1 PGN (PF=0xEF < 240). The wire ID encodes destination in PS.
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        var message = new J1939Message(pgn: 0xEF00u, payload: payload, priority: 6,
            destinationAddress: 0x44);

        var receiveTask = WaitForMessageAsync(receiver,
            m => m.Pgn == 0xEF00u && m.SourceAddress == 0x33, ShortTimeout);
        await sender.SendAsync(message).WithTimeout(ShortTimeout);

        var received = await receiveTask;
        received.Pgn.Should().Be(0xEF00u);
        received.SourceAddress.Should().Be(0x33);
        received.DestinationAddress.Should().Be(0x44);
        received.Payload.ToArray().Should().Equal(payload);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-002: SPN scale/offset extraction.
    // ---------------------------------------------------------------------------------------

    // Well-known SPN 190 (Engine Speed, PGN 61444 / EEC1): 16-bit little-endian value at
    // byte offset 3, resolution 0.125 rpm/bit, offset 0. 8000 rpm ⇒ raw 64000 ⇒ 0x00 0xFA.
    [Fact]
    public void Spn_ExtractsScaleAndOffset()
    {
        var payload = new byte[8];
        // Byte 3 = 0x00, byte 4 = 0xFA (little-endian raw 64000).
        payload[3] = 0x00; payload[4] = 0xFA;
        double engineSpeed = J1939Spn.Extract(payload, byteOffset: 3, startBit: 0,
            bitLength: 16, resolution: 0.125, offset: 0.0);
        engineSpeed.Should().BeApproximately(8000.0, 0.001);
    }

    // Cross-byte-boundary 4-bit SPN with an offset (e.g. a temperature-style transform).
    [Fact]
    public void Spn_ExtractsCrossByteWithOffset()
    {
        var payload = new byte[] { 0b1111_0000, 0b0000_1010 };
        // Field spans byte 0 bits 4..7 and byte 1 bits 0..3 = 8 bits, little-endian.
        // = ( (0b0000_1010 & 0x0F) << 4 ) | ( (0b1111_0000 >> 4) & 0x0F ) = 0xAF = 175.
        double physical = J1939Spn.Extract(payload, byteOffset: 0, startBit: 4,
            bitLength: 8, resolution: 1.0, offset: -40.0);
        physical.Should().Be(175 - 40.0);
    }

    // Round-trip via WriteRaw so encoders and decoders are consistent.
    [Fact]
    public void Spn_WriteRaw_RoundTripsWithExtract()
    {
        var payload = new byte[8];
        J1939Spn.WriteRaw(payload, byteOffset: 2, startBit: 3, bitLength: 12, rawValue: 0xABC);
        J1939Spn.ExtractRaw(payload, byteOffset: 2, startBit: 3, bitLength: 12).Should().Be(0xABC);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-003: Address claiming with NAME arbitration (winner keeps address, loser goes
    // to Cannot-Claim).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AddressClaim_TwoNodes_SameAddress_LowerNameWins()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        // Lower identity number ⇒ lower 64-bit NAME ⇒ higher claim priority (§4.4.3.2).
        var winnerName = Name(identity: 0x0000AA);
        var loserName = Name(identity: 0x0000BB);

        // Shrink the arbitration window so the test finishes quickly (default is 250 ms).
        var opts = (J1939NodeOptions optsFor) => optsFor;
        var optsWinner = new J1939NodeOptions(winnerName) { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };
        var optsLoser = new J1939NodeOptions(loserName) { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };

        using var winner = J1939Node.Open(busA, optsWinner);
        using var loser = J1939Node.Open(busB, optsLoser);

        // Both try 0x50 essentially concurrently; the actor loops each process the peer's
        // announcement and one of them yields.
        var winnerTask = winner.ClaimAddressAsync(0x50);
        var loserTask = loser.ClaimAddressAsync(0x50);

        // Winner (numerically-lower NAME) must succeed.
        await winnerTask.WithTimeout(ShortTimeout);
        winner.ClaimState.Should().Be(J1939ClaimState.Claimed);
        winner.Address.Should().Be((byte)0x50);

        // Loser must fault with J1939CannotClaimException per FR-J1939-004.
        Func<Task> act = () => loserTask.WithTimeout(ShortTimeout);
        var ex = (await act.Should().ThrowAsync<J1939CannotClaimException>()).Which;
        ex.PreferredAddress.Should().Be((byte)0x50);
        loser.ClaimState.Should().Be(J1939ClaimState.CannotClaim);
        loser.Address.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-004: Cannot-Claim broadcasts SA=0xFE.
    // ---------------------------------------------------------------------------------------

    // A node whose preferred address is contested by a peer with a lower NAME MUST broadcast
    // Cannot Claim Address (PGN 0xEE00, SA=0xFE) per SAE J1939-81 §4.4.3.4. We observe the
    // raw frame on the bus so the assertion is independent of the node's own transitions.
    [Fact]
    public async Task CannotClaim_BroadcastsWithNullSourceAddress()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2); // spectator

        var cannotClaimSeen = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        busC.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (J1939Pgn.IsAddressClaim(fields.Pgn) && fields.SourceAddress == J1939Pgn.NullAddress)
                cannotClaimSeen.TrySetResult((uint)e.CanFrame.ID);
        };

        var winnerOpts = new J1939NodeOptions(Name(0x000010)) { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };
        var loserOpts = new J1939NodeOptions(Name(0x000020)) { ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(200) };

        using var winner = J1939Node.Open(busA, winnerOpts);
        using var loser = J1939Node.Open(busB, loserOpts);

        var winnerTask = winner.ClaimAddressAsync(0x60);
        var loserTask = loser.ClaimAddressAsync(0x60);

        await winnerTask.WithTimeout(ShortTimeout);
        Func<Task> act = () => loserTask.WithTimeout(ShortTimeout);
        await act.Should().ThrowAsync<J1939CannotClaimException>();

        var canId = await cannotClaimSeen.Task.AsTaskWithTimeout(ShortTimeout);
        var decomposed = J1939Id.Decompose(canId);
        decomposed.SourceAddress.Should().Be(J1939Pgn.NullAddress);
        decomposed.PduSpecific.Should().Be(J1939Pgn.GlobalAddress);
        J1939Pgn.IsAddressClaim(decomposed.Pgn).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-005: Request-PGN (PGN 0xEA00) send/receive.
    // ---------------------------------------------------------------------------------------

    // A requester sends Request-PGN(0xFEF1) to the global address; the responder application
    // observes the request on MessageReceived and answers with the requested PGN. The
    // requester's inbox then sees the answer.
    [Fact]
    public async Task RequestPgn_ResponderReceivesAndAppReplies()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);

        using var requester = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        using var responder = J1939Node.Open(busB, new J1939NodeOptions(Name(2)));

        await requester.ClaimAddressAsync(0x71).WithTimeout(ShortTimeout);
        await responder.ClaimAddressAsync(0x72).WithTimeout(ShortTimeout);

        const uint requestedPgn = 0xFEF1u;
        var answerPayload = new byte[] { 0x01, 0x02, 0x03 };

        // Responder listens for Request-PGN and answers with the requested PGN.
        responder.MessageReceived += async (_, msg) =>
        {
            if (msg.Pgn != J1939Pgn.Request) return;
            if (msg.Payload.Length < 3) return;
            uint askedFor = (uint)(msg.Payload.Span[0]
                | (msg.Payload.Span[1] << 8)
                | (msg.Payload.Span[2] << 16));
            if (askedFor != requestedPgn) return;
            try
            {
                await responder.SendAsync(new J1939Message(requestedPgn, answerPayload));
            }
            catch { /* observed via BackgroundExceptionOccurred */ }
        };

        var answerTask = WaitForMessageAsync(requester,
            m => m.Pgn == requestedPgn && m.SourceAddress == 0x72,
            ShortTimeout);

        await requester.RequestPgnAsync(requestedPgn).WithTimeout(ShortTimeout);
        var answer = await answerTask;
        answer.Payload.ToArray().Should().Equal(answerPayload);
    }

    // ---------------------------------------------------------------------------------------
    // FR-J1939-006: > 8-byte payload routes through J1939-TP; <= 8-byte payload is direct.
    // ---------------------------------------------------------------------------------------

    // A ≤ 8-byte payload MUST NOT trigger any TP.CM/TP.DT frame on the bus; instead exactly
    // one direct 29-bit frame carries the PGN.
    [Fact]
    public async Task Send_SmallPayload_UsesSingleFramePath()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var spectator = Open(session, 2);

        using var sender = J1939Node.Open(busA, new J1939NodeOptions(Name(1)));
        await sender.ClaimAddressAsync(0x81).WithTimeout(ShortTimeout);

        int tpFrames = 0;
        int singleFrames = 0;
        spectator.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != 0x81) return;
            if (J1939Pgn.IsTransportCm(fields.Pgn) || J1939Pgn.IsTransportDt(fields.Pgn))
                Interlocked.Increment(ref tpFrames);
            else if (fields.Pgn == 0xFEF2u)
                Interlocked.Increment(ref singleFrames);
        };

        await sender.SendAsync(new J1939Message(0xFEF2u, new byte[] { 1, 2, 3, 4, 5, 6 }))
            .WithTimeout(ShortTimeout);

        // Wait deterministically for exactly one single-frame observation instead of relying
        // on a fixed 50 ms sleep (Copilot 3600424648): the fixed delay was flaky on slow CI
        // runners, and the previous `> 0` assertion silently accepted duplicates.
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (Volatile.Read(ref singleFrames) < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Volatile.Read(ref tpFrames).Should().Be(0,
            "a ≤ 8-byte payload must not use J1939-TP");
        Volatile.Read(ref singleFrames).Should().Be(1,
            "exactly one direct 29-bit frame must carry the PGN");
    }

    // A > 8-byte payload broadcast MUST use J1939-TP.BAM. We watch for TP.CM frames from the
    // sender on the bus and require the receiver's application PGN to arrive on
    // MessageReceived (proving the whole TP session ran through and reassembled).
    [Fact]
    public async Task Send_LargePayload_UsesJ1939TpBamPath()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var spectator = Open(session, 2);

        // Shorten Th so the multi-frame test runs in <1s while still exercising the timer.
        var senderOpts = new J1939NodeOptions(Name(1))
        {
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(5)),
        };
        var receiverOpts = new J1939NodeOptions(Name(2))
        {
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(5)),
        };

        using var sender = J1939Node.Open(busA, senderOpts);
        using var receiver = J1939Node.Open(busB, receiverOpts);

        await sender.ClaimAddressAsync(0x91).WithTimeout(ShortTimeout);
        await receiver.ClaimAddressAsync(0x92).WithTimeout(ShortTimeout);

        int tpCmSeen = 0;
        spectator.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.SourceAddress != 0x91) return;
            if (J1939Pgn.IsTransportCm(fields.Pgn)) Interlocked.Increment(ref tpCmSeen);
        };

        var payload = new byte[20];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(0x40 + i);

        var recvTask = WaitForMessageAsync(receiver,
            m => m.Pgn == 0xFECAu && m.Payload.Length == 20, ShortTimeout);

        await sender.SendAsync(new J1939Message(0xFECAu, payload,
            destinationAddress: 0xFF)).WithTimeout(ShortTimeout);

        var received = await recvTask;
        received.Payload.ToArray().Should().Equal(payload);
        received.SourceAddress.Should().Be(0x91);
        received.WasMultiFrame.Should().BeTrue();
        Volatile.Read(ref tpCmSeen).Should().BeGreaterThan(0,
            ">8-byte payload must route through J1939-TP (a TP.CM announce must appear on the bus)");
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600377721 regression: after a successful address claim the node MUST accept
    // directed TP.CM traffic to the claimed SA. Before the fix J1939NodeImpl kept its internal
    // IJ1939TpChannel bound to the 0xFE placeholder SA even after ClaimState==Claimed, so
    // J1939TpChannel's `destination == SA || 0xFF` filter dropped every directed CM to the
    // claimed address.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task DirectedTpCm_ToClaimedAddress_IsReceivedAfterClaim()
    {
        var session = NewSession();
        using var busA = Open(session, 0); // peer: raw J1939-TP sender
        using var busB = Open(session, 1); // node under test

        // The receiver is a J1939 node — the whole point is to verify the *node* reassembles
        // and surfaces the directed multi-frame PDU on MessageReceived.
        using var receiver = J1939Node.Open(busB, new J1939NodeOptions(Name(2))
        {
            TransportOptions = new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(5)),
        });
        await receiver.ClaimAddressAsync(0xA0).WithTimeout(ShortTimeout);
        receiver.ClaimState.Should().Be(J1939ClaimState.Claimed);
        receiver.Address.Should().Be((byte)0xA0);

        // Peer sends a directed TP.CM (>8 bytes) targeting the claimed SA 0xA0. Uses a raw
        // J1939-TP channel from a different SA so the frames actually travel across the
        // virtual bus and hit the node's transport RX filter.
        using var peerTp = CanKit.Pro.J1939Tp.J1939Tp.Open(busA, sourceAddress: 0x55,
            new J1939TpOptions().With(th: TimeSpan.FromMilliseconds(5)));

        var payload = new byte[24];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(0xB0 + i);

        var recvTask = WaitForMessageAsync(receiver,
            m => m.Pgn == 0xEF00u && m.Payload.Length == payload.Length && m.SourceAddress == 0x55,
            ShortTimeout);

        await peerTp.SendCmAsync(pgn: 0xEF00u, destinationAddress: 0xA0, payload)
            .WithTimeout(ShortTimeout);

        var received = await recvTask;
        received.Payload.ToArray().Should().Equal(payload);
        received.SourceAddress.Should().Be(0x55);
        received.DestinationAddress.Should().Be(0xA0);
        received.WasMultiFrame.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600440955 regression: cancelling ClaimAddressAsync during the arbitration
    // window MUST tear down the pending claim on the actor and prevent the arbitration timer
    // from later committing the address. Before the fix, the cancellation registration only
    // called TrySetCanceled on the returned task; OnClaimAnnounceElapsed still fired and
    // moved ClaimState to Claimed, silently contradicting the observed cancellation.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ClaimAddressAsync_CancelDuringArbitration_TearsDownPendingClaim()
    {
        var session = NewSession();
        using var busA = Open(session, 0);

        // Long arbitration window so the test can cancel comfortably in the middle. 500 ms is
        // well above the actor scheduling jitter we need to observe.
        var opts = new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(500),
        };
        using var node = J1939Node.Open(busA, opts);

        using var cts = new CancellationTokenSource();
        var claimTask = node.ClaimAddressAsync(0x33, cts.Token);

        // Give the actor a beat to enter Claiming so we know we cancel mid-arbitration and
        // not before BeginClaim has run.
        for (int i = 0; i < 20 && node.ClaimState != J1939ClaimState.Claiming; i++)
            await Task.Delay(10);
        node.ClaimState.Should().Be(J1939ClaimState.Claiming);

        cts.Cancel();

        // The task itself must complete as cancelled.
        Func<Task> awaitClaim = () => claimTask.WithTimeout(ShortTimeout);
        await awaitClaim.Should().ThrowAsync<TaskCanceledException>();

        // Wait past the original arbitration window so any surviving timer would have fired.
        await Task.Delay(700);

        // The node MUST NOT have silently committed to the cancelled address.
        node.ClaimState.Should().NotBe(J1939ClaimState.Claimed);
        node.Address.Should().BeNull();

        // A fresh claim must still work (i.e. teardown left the state machine consistent).
        await node.ClaimAddressAsync(0x44).WithTimeout(ShortTimeout);
        node.ClaimState.Should().Be(J1939ClaimState.Claimed);
        node.Address.Should().Be((byte)0x44);
    }

    // ---------------------------------------------------------------------------------------
    // Bugbot 3600377725 regression: starting a fresh ClaimAddressAsync on an already-claimed
    // node MUST invalidate the old SA immediately. SendAsync must reject application traffic
    // (throw J1939NoAddressException) until the new claim reaches Claimed again, otherwise the
    // node keeps transmitting with the old SA while its address-claim frame advertises a
    // different preferred one on the wire.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ReClaim_RejectsSendUntilNewClaimSucceeds()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1); // spectator: watches which SAs appear on the wire

        // Give the arbitration window enough room that we can observe the mid-claim gap even on
        // a fast Virtual bus. 500 ms is well above CI jitter but short enough to keep the test
        // fast.
        var opts = new J1939NodeOptions(Name(1))
        {
            ClaimAnnounceTimeout = TimeSpan.FromMilliseconds(500),
        };
        using var node = J1939Node.Open(busA, opts);

        // Initial claim -> we hold 0x11.
        await node.ClaimAddressAsync(0x11).WithTimeout(ShortTimeout);
        node.ClaimState.Should().Be(J1939ClaimState.Claimed);

        // A send with the initial claim succeeds; SA on the wire must be 0x11.
        byte? observedSa = null;
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.Pgn == 0xFEF3u) observedSa = fields.SourceAddress;
        };
        await node.SendAsync(new J1939Message(0xFEF3u, new byte[] { 1, 2, 3 })).WithTimeout(ShortTimeout);
        await Task.Delay(50);
        observedSa.Should().Be((byte)0x11);

        // Start a re-claim to a different preferred SA — do NOT await yet so we can inspect
        // the mid-claim behavior. The state must transition out of Claimed immediately.
        var reclaimTask = node.ClaimAddressAsync(0x22);

        // Give the actor a beat to process BeginClaim.
        for (int i = 0; i < 20 && node.ClaimState == J1939ClaimState.Claimed; i++)
            await Task.Delay(10);
        node.ClaimState.Should().NotBe(J1939ClaimState.Claimed,
            "starting a new claim must clear the previous Claimed state so old-SA traffic is gated off");
        node.Address.Should().BeNull(
            "the previous claimed address must be invalidated before the new preferred SA is announced");

        // SendAsync MUST reject application traffic while the claim is in-flight. Before the
        // fix, SendCoreAsync only checked _addressStore >= 0, so this send would silently go
        // out with the *previous* SA (0x11) while claim frames advertised 0x22.
        Func<Task> sendMidClaim = () => node.SendAsync(new J1939Message(0xFEF4u, new byte[] { 4, 5, 6 }));
        await sendMidClaim.Should().ThrowAsync<J1939NoAddressException>();

        // Once the new claim completes, application traffic MUST resume on the new SA.
        await reclaimTask.WithTimeout(ShortTimeout);
        node.ClaimState.Should().Be(J1939ClaimState.Claimed);
        node.Address.Should().Be((byte)0x22);

        byte? postSa = null;
        busB.FrameObserved += (_, e) =>
        {
            if (!e.CanFrame.IsExtendedFrame) return;
            var fields = J1939Id.Decompose((uint)e.CanFrame.ID);
            if (fields.Pgn == 0xFEF5u) postSa = fields.SourceAddress;
        };
        await node.SendAsync(new J1939Message(0xFEF5u, new byte[] { 7, 8, 9 }))
            .WithTimeout(ShortTimeout);
        await Task.Delay(50);
        postSa.Should().Be((byte)0x22);
    }
}

internal static class J1939NodeTestExtensions
{
    public static async Task<T> AsTaskWithTimeout<T>(this Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task) throw new TimeoutException($"Operation timed out after {timeout}.");
        return await task;
    }

    public static async Task WithTimeout(this Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task) throw new TimeoutException($"Operation timed out after {timeout}.");
        await task;
    }
}
