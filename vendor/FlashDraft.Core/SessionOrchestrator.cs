using System.Net;
using System.Security.Cryptography;
using FlashDraft.Core;
using FlashDraft.Crypto;

namespace FlashDraft.Transport;

/// <summary>
/// Owns the local ephemeral key pair and drives a full transfer session.
/// There is no signaling server: peers find each other on a LAN through the
/// candidates in the offer code, and on the public Internet only by exchanging
/// that code out of band before UDP hole punching.
/// </summary>
public sealed class SessionOrchestrator
{
    private readonly EphemeralKeyPair _keys;
    private readonly IStunClient _stun;
    private readonly int _lanPort;
    private readonly int _wanPort;

    public SessionOrchestrator(
        IStunClient? stun = null,
        EphemeralKeyPair? keys = null,
        int? lanPort = null,
        int? wanPort = null)
    {
        _keys = keys ?? new EphemeralKeyPair();
        _stun = stun ?? new StunClient();
        _lanPort = lanPort ?? ProtocolConstants.MulticastPort + 1;
        _wanPort = wanPort ?? ProtocolConstants.WanDataPort;
    }

    public byte[] LocalPublicKey => _keys.PublicKey;

    public async Task<SessionOffer> CreateOfferAsync(CancellationToken cancellationToken)
    {
        string? reflexive = await _stun.GetReflexiveEndpointAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> lan = LanCandidates.Collect(_lanPort);
        return new SessionOffer(
            Guid.NewGuid(),
            _keys.PublicKey,
            DateTimeOffset.UtcNow.AddMinutes(ProtocolConstants.ConnectionCodeTtlMinutes),
            RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
            lan,
            reflexive);
    }

    public string Encode(SessionOffer offer) => ConnectionCode.EncodeOffer(offer);

    public SessionOffer Parse(string code, DateTimeOffset now) => ConnectionCode.DecodeOffer(code, now);

    public (SessionKeys Keys, Guid SessionId) Bind(SessionOffer local, SessionOffer remote)
    {
        SessionKeys keys = SecureSession.Bind(_keys, local, remote, out Guid sid);
        return (keys, sid);
    }

    public async Task SendAsync(
        SessionOffer remoteOffer,
        Guid sessionId,
        SessionKeys keys,
        IReadOnlyList<FileDescriptor> files,
        IReadOnlyList<string> sourcePaths,
        IProgress<ProgressSnapshot> progress,
        bool preferWan,
        CancellationToken cancellationToken)
    {
        if (preferWan)
        {
            string remote = remoteOffer.ReflexiveEndpoint
                ?? throw new InvalidOperationException(
                    "The remote offer has no STUN reflexive endpoint. Regenerate it on a network with UDP egress.");
            PunchedEndpoint? endpoint = await new UdpHolePunchingService()
                .PunchAsync(_wanPort, remote, sessionId, cancellationToken).ConfigureAwait(false);
            if (endpoint is null)
            {
                throw new InvalidOperationException(
                    "Hole punching failed (possibly symmetric NAT). Exchange a fresh offer code, use a LAN, or configure manual port mapping. TURN relay is not implemented.");
            }

            using (endpoint.Client)
            {
                var service = new UdpReliableTransferService(
                    endpoint.Client, endpoint.Remote, keys, sessionId);
                await service.SendAsync(files, sourcePaths, progress, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        string lan = remoteOffer.LanCandidates.FirstOrDefault()
            ?? throw new InvalidOperationException("The remote offer has no LAN candidate.");
        (string host, int port) = Split(lan);
        var tcp = new LanTcpTransferService(keys, port);
        await tcp.SendAsync(
            BuildSession(sessionId, remoteOffer), files, sourcePaths, lan,
            progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReceiveAsync(
        SessionOffer localOffer,
        Guid sessionId,
        SessionKeys keys,
        string destinationDirectory,
        IProgress<ProgressSnapshot> progress,
        bool preferWan,
        CancellationToken cancellationToken)
    {
        TransferSession session = BuildSession(sessionId, localOffer);
        if (preferWan)
        {
            // The responder does not know the peer's reflexive address yet; it
            // learns it from the first inbound probe.
            PunchedEndpoint? endpoint = await new UdpHolePunchingService()
                .PunchAsync(_wanPort, null, sessionId, cancellationToken).ConfigureAwait(false);
            if (endpoint is null)
            {
                throw new InvalidOperationException(
                    "Hole punching failed (possibly symmetric NAT). Exchange a fresh offer code, use a LAN, or configure manual port mapping. TURN relay is not implemented.");
            }

            using (endpoint.Client)
            {
                var service = new UdpReliableTransferService(
                    endpoint.Client, endpoint.Remote, keys, sessionId);
                await service.ReceiveAsync(destinationDirectory, progress, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var tcp = new LanTcpTransferService(keys, _lanPort);
        await tcp.ReceiveAsync(session, destinationDirectory, progress, cancellationToken).ConfigureAwait(false);
    }

    private TransferSession BuildSession(Guid sessionId, SessionOffer offer) =>
        new(sessionId, _keys.PublicKey, DateTimeOffset.UtcNow, offer.ExpiresAt,
            offer.OneTimeToken, offer.LanCandidates, offer.ReflexiveEndpoint);

    private static (string Host, int Port) Split(string endpoint)
    {
        int separator = endpoint.LastIndexOf(':');
        if (separator < 0)
        {
            throw new FormatException("Endpoint must be host:port.");
        }

        return (endpoint.Substring(0, separator), int.Parse(endpoint.Substring(separator + 1)));
    }
}
