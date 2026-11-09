using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FlashDraft.Core;
using FlashDraft.Crypto;

namespace FlashDraft.Transport;

/// <summary>
/// Owns the local ephemeral key pair and drives a full transfer session.
/// There is no signaling server: peers find each other on a LAN through the
/// candidates in the offer code, and on the public Internet only by exchanging
/// that code out of band before dual-stack UDP hole punching.
/// </summary>
public sealed class SessionOrchestrator
{
    private readonly EphemeralKeyPair _keys;
    private readonly IStunClient _stun;
    private readonly int _lanPort;
    private readonly int _wanPort;
    private NatType _localNat = NatType.Unknown;
    private SessionOffer? _remoteOffer;
    private UdpClient? _punchV4;
    private UdpClient? _punchV6;

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

    /// <summary>
    /// Generates an offer. With the real STUN client this binds the punching
    /// sockets first and performs five-attempt, dual-stack reflexive discovery
    /// through those same sockets, so the reflexive port is exactly the port
    /// the peer will dial. It then embeds the ordered endpoint set in a v2 code.
    /// </summary>
    public async Task<SessionOffer> CreateOfferAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lan = LanCandidates.Collect(_lanPort);
        IReadOnlyList<string> endpoints = Array.Empty<string>();
        string? reflexive = null;
        string? natHint = null;

        if (_stun is StunClient)
        {
            UdpClient v4 = CreateBound(AddressFamily.InterNetwork);
            int port = ((IPEndPoint)v4.Client.LocalEndPoint!).Port;
            UdpClient? v6 = null;
            try
            {
                v6 = CreateBound(AddressFamily.InterNetworkV6, port);
            }
            catch (SocketException)
            {
                v6 = null;
            }

            (string? v4Endpoint, List<int> v4Ports) =
                await DiscoverBoundAsync(v4, AddressFamily.InterNetwork, cancellationToken)
                    .ConfigureAwait(false);
            string? v6Endpoint = null;
            if (v6 is not null)
            {
                (v6Endpoint, _) = await DiscoverBoundAsync(
                    v6, AddressFamily.InterNetworkV6, cancellationToken).ConfigureAwait(false);
            }

            reflexive = v4Endpoint;
            _localNat = NatClassifier.FromSamples(v4Ports);
            natHint = _localNat.ToString();
            endpoints = EndpointSet.Build(v6Endpoint, v4Endpoint, lan).Ordered;
            _punchV4 = v4;
            _punchV6 = v6;
        }
        else
        {
            reflexive = await _stun.GetReflexiveEndpointAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SessionOffer(
            Guid.NewGuid(),
            _keys.PublicKey,
            DateTimeOffset.UtcNow.AddMinutes(ProtocolConstants.ConnectionCodeTtlMinutes),
            RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
            lan,
            reflexive)
        {
            Endpoints = endpoints,
            NatTypeHint = natHint,
        };
    }

    /// <summary>
    /// Keeps the NAT mapping alive while the generated offer code is valid,
    /// using the same bound socket. Cancel once the session is established.
    /// </summary>
    public async Task StartKeepAliveAsync(CancellationToken cancellationToken)
    {
        bool owned = _punchV4 is null;
        UdpClient socket = _punchV4 ?? new UdpClient(AddressFamily.InterNetwork);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] request = StunClient.BuildBindingRequest();
                try
                {
                    await socket.SendAsync(
                        request, request.Length,
                        ProtocolConstants.DefaultStunHost,
                        ProtocolConstants.DefaultStunPort).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // Best effort.
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(ProtocolConstants.OfferKeepAliveSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            if (owned) socket.Dispose();
        }
    }

    public string Encode(SessionOffer offer) => ConnectionCode.EncodeOffer(offer);

    public SessionOffer Parse(string code, DateTimeOffset now) => ConnectionCode.DecodeOffer(code, now);

    public (SessionKeys Keys, Guid SessionId) Bind(SessionOffer local, SessionOffer remote)
    {
        _remoteOffer = remote;
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
            PunchedEndpoint endpoint = remoteOffer.Endpoints.Count > 0
                ? await PunchV2Async(remoteOffer, sessionId, isInitiator: true, cancellationToken)
                    .ConfigureAwait(false)
                : await PunchLegacyAsync(remoteOffer.ReflexiveEndpoint, sessionId, cancellationToken)
                    .ConfigureAwait(false);

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
            PunchedEndpoint endpoint = localOffer.Endpoints.Count > 0
                ? await PunchV2Async(localOffer, sessionId, isInitiator: false, cancellationToken)
                    .ConfigureAwait(false)
                : await PunchLegacyAsync(null, sessionId, cancellationToken).ConfigureAwait(false);

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

    private async Task<PunchedEndpoint> PunchV2Async(
        SessionOffer offer,
        Guid sessionId,
        bool isInitiator,
        CancellationToken cancellationToken)
    {
        SessionOffer? peer = isInitiator ? offer : (_remoteOffer ?? offer);
        IReadOnlyList<string> remotes = EndpointSet.FromOffer(peer);
        var options = new PunchOptions(
            sessionId,
            offer.OneTimeToken,
            remotes,
            EnableIpv6: true,
            IsInitiator: isInitiator,
            LocalNat: _localNat,
            RemoteNat: ParseNat(peer.NatTypeHint),
            SharedV4: _punchV4,
            SharedV6: _punchV6);

        PunchOutcome outcome = await new WanPunchService()
            .PunchAsync(options, cancellationToken).ConfigureAwait(false);
        _punchV4 = null;
        _punchV6 = null;
        if (!outcome.Succeeded || outcome.Endpoint is null)
        {
            throw new PunchFailedException(
                outcome.Failure ?? PunchDiagnostics.Build(_localNat, options.RemoteNat, "probe"));
        }

        return outcome.Endpoint;
    }

    private async Task<PunchedEndpoint> PunchLegacyAsync(
        string? remoteEndpoint,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        PunchedEndpoint? endpoint = await new UdpHolePunchingService()
            .PunchAsync(_wanPort, remoteEndpoint, sessionId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            throw new PunchFailedException(
                PunchDiagnostics.Build(_localNat, NatType.Unknown, "probe"));
        }

        return endpoint;
    }

    private static async Task<(string? Endpoint, List<int> Ports)> DiscoverBoundAsync(
        UdpClient socket,
        AddressFamily family,
        CancellationToken cancellationToken)
    {
        var ports = new List<int>();
        string? endpoint = null;
        string[] servers = ProtocolConstants.StunServerEndpoints;
        for (int attempt = 0; attempt < ProtocolConstants.StunMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string host, int serverPort) = ParseServer(servers[attempt % servers.Length]);
            string? mapped = await StunClient.QueryBoundAsync(
                socket, new StunServer(host, serverPort), family, cancellationToken)
                .ConfigureAwait(false);
            if (mapped is not null)
            {
                IPEndPoint? parsed = EndpointSet.Parse(mapped);
                if (parsed is not null)
                {
                    endpoint ??= EndpointSet.Normalize(mapped);
                    ports.Add(parsed.Port);
                }
            }

            if (endpoint is not null && ports.Count >= 2) break;
            if (attempt + 1 < ProtocolConstants.StunMaxAttempts)
            {
                await Task.Delay(ProtocolConstants.StunBackoffMs[attempt], cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return (endpoint, ports);
    }

    private static UdpClient CreateBound(AddressFamily family, int port = 0)
    {
        var client = new UdpClient(family);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        IPAddress address = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        client.Client.Bind(new IPEndPoint(address, port));
        return client;
    }

    private static (string Host, int Port) ParseServer(string entry)
    {
        int colon = entry.LastIndexOf(':');
        return (entry.Substring(0, colon), int.Parse(entry.Substring(colon + 1)));
    }

    private static NatType ParseNat(string? hint) =>
        hint is not null && Enum.TryParse<NatType>(hint, out NatType value)
            ? value
            : NatType.Unknown;

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
