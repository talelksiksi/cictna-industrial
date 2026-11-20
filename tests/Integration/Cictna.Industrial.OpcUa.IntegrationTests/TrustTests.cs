using System.Diagnostics;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.IntegrationTests;

/// <summary>
/// Phase 3 exit criteria: trusted, rejected, expired and renewed certificates, on both sides of
/// the connection (the client checking the server, the server checking the client), with
/// certificate stores on disk as an installation would use them.
/// </summary>
public sealed class TrustTests : IDisposable
{
    private const string ClientUri = "urn:cictna:test:trust-client";
    private const string ServerUri = "urn:cictna:test:trust-server";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cictna-trust-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort on a temp folder
        }
    }

    private CertificateStore Store(string name) => new(Path.Combine(_root, name));

    private static Task<OpcUaServer> StartServerAsync(CertificateStore? store = null, ApplicationCertificate? certificate = null,
        bool acceptAnyClient = false, OwnCertificateSettings? own = null) =>
        OpcUaServer.StartAsync(new OpcUaServerOptions
        {
            Port = TestServer.FreePort(),
            ApplicationUri = ServerUri,
            ApplicationName = "Trust Test Server",
            CertificateStore = store,
            Certificate = certificate,
            OwnCertificate = own,
            AcceptAnyClientCertificateForDevelopment = acceptAnyClient,
            TrustListRefreshInterval = TimeSpan.FromMilliseconds(200),
        });

    private static OpcUaClientOptions ClientOptions(CertificateStore? store, ApplicationCertificate? certificate = null) => new()
    {
        ApplicationUri = ClientUri,
        ApplicationName = "Trust Test Client",
        CertificateStore = store,
        Certificate = certificate,
        SecurityMode = MessageSecurityMode.SignAndEncrypt,
        SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Reconnect = new ReconnectOptions { Enabled = false },
    };

    private static ApplicationCertificate ClientCertificate(CertificateStore store) =>
        store.GetOrCreateApplicationCertificate(new OwnCertificateSettings(ClientUri, "Trust Test Client"));

    private static async Task ReadSomethingAsync(OpcUaClient client) =>
        Assert.True((await client.ReadAsync(WellKnownNodes.ServerCurrentTime, TestContext.Current.CancellationToken)).Status.IsGood);

    // ---- trusted and rejected, the client checking the server -----------------------------------

    [Fact]
    public async Task TheClientRefusesAnUntrustedServerAndRecordsItUntilAnOperatorTrustsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverStore = Store("server");
        var clientStore = Store("client");
        serverStore.Trust(ClientCertificate(clientStore).Certificate);
        await using var server = await StartServerAsync(serverStore);
        var serverThumbprint = new ApplicationCertificate(server.ServerCertificate!, []).Thumbprint;

        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore));
        var refused = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.LocalEndpointUrl, ct));
        Assert.Equal(StatusCodes.BadCertificateUntrusted, refused.StatusCode);
        Assert.Contains(serverThumbprint, refused.Message, StringComparison.Ordinal);
        Assert.Contains(clientStore.RejectedCertsPath, refused.Message, StringComparison.Ordinal);
        Assert.Equal(serverThumbprint, Assert.Single(clientStore.Rejected).Thumbprint);
        Assert.Equal(ConnectionState.Disconnected, client.State);

        // The operator moves it to trusted/certs; the next connection reads the store again.
        Assert.True(clientStore.TrustRejected(serverThumbprint));
        Assert.Empty(clientStore.Rejected);
        await client.ConnectAsync(server.LocalEndpointUrl, ct);
        await ReadSomethingAsync(client);
    }

    // ---- trusted and rejected, the server checking the client -------------------------------------

    [Fact]
    public async Task TheServerRefusesAnUntrustedClientRecordsItAndAcceptsItOnceTrustedWithoutRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverStore = Store("server");
        var clientStore = Store("client");
        var clientCertificate = ClientCertificate(clientStore);
        await using var server = await StartServerAsync(serverStore);
        clientStore.Trust(server.ServerCertificate!);

        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore));
        var refused = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.LocalEndpointUrl, ct));
        Assert.True(refused.StatusCode.IsBad);
        // Our check of the server passed, so the message points at the server's side.
        Assert.Contains("server refused this client's certificate", refused.Message, StringComparison.Ordinal);
        Assert.Contains(clientCertificate.Thumbprint, refused.Message, StringComparison.Ordinal);
        Assert.Empty(clientStore.Rejected);

        // The server saves the refused client certificate for review...
        await WaitUntilAsync(() => serverStore.Rejected.Any(c => c.Thumbprint == clientCertificate.Thumbprint), "client certificate in the server's rejected folder");
        // ...and once it is moved to trusted/certs, the running server accepts the client.
        Assert.True(serverStore.TrustRejected(clientCertificate.Thumbprint));
        await RetryConnectAsync(client, server.LocalEndpointUrl);
        await ReadSomethingAsync(client);
    }

    [Fact]
    public async Task WithoutAStoreTheServerRefusesSecureClientsUnlessDevelopmentModeIsOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientStore = Store("client");
        var serverCertificate = ApplicationCertificate.Create(ServerUri, "Trust Test Server", dnsNames: ["localhost"]);
        clientStore.Trust(serverCertificate.Certificate);

        await using (var strict = await StartServerAsync(certificate: serverCertificate))
        await using (var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore)))
        {
            await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(strict.LocalEndpointUrl, ct));
        }

        await using var lab = await StartServerAsync(certificate: serverCertificate, acceptAnyClient: true);
        await using var labClient = await OpcUaClient.CreateAsync(ClientOptions(clientStore));
        await labClient.ConnectAsync(lab.LocalEndpointUrl, ct);
        await ReadSomethingAsync(labClient);
    }

    // ---- expired ------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExpiredServerCertificateIsRefusedEvenWhenTrusted()
    {
        var ct = TestContext.Current.CancellationToken;
        var expired = ApplicationCertificate.Create(ServerUri, "Expired Server", dnsNames: ["localhost"],
            notBefore: DateTime.UtcNow.AddDays(-400), validity: TimeSpan.FromDays(365));
        Assert.True(expired.NotAfter < DateTime.UtcNow);
        await using var server = await StartServerAsync(certificate: expired, acceptAnyClient: true);
        var clientStore = Store("client");
        clientStore.Trust(expired.Certificate);

        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore));
        var refused = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.LocalEndpointUrl, ct));
        Assert.Equal(StatusCodes.BadCertificateTimeInvalid, refused.StatusCode);
    }

    [Fact]
    public async Task ANotYetValidServerCertificateIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var future = ApplicationCertificate.Create(ServerUri, "Future Server", dnsNames: ["localhost"], notBefore: DateTime.UtcNow.AddDays(10));
        await using var server = await StartServerAsync(certificate: future, acceptAnyClient: true);
        var clientStore = Store("client");
        clientStore.Trust(future.Certificate);
        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore));
        var refused = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.LocalEndpointUrl, ct));
        Assert.Equal(StatusCodes.BadCertificateTimeInvalid, refused.StatusCode);
    }

    [Fact]
    public async Task AnExpiredClientCertificateIsRefusedByTheServerEvenWhenTrusted()
    {
        var ct = TestContext.Current.CancellationToken;
        var expired = ApplicationCertificate.Create(ClientUri, "Expired Client",
            notBefore: DateTime.UtcNow.AddDays(-400), validity: TimeSpan.FromDays(365));
        var serverStore = Store("server");
        serverStore.Trust(expired.Certificate);
        await using var server = await StartServerAsync(serverStore);
        var clientStore = Store("client");
        clientStore.Trust(server.ServerCertificate!);

        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore, expired));
        var refused = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.LocalEndpointUrl, ct));
        Assert.Contains("server refused this client's certificate", refused.Message, StringComparison.Ordinal);
    }

    // ---- renewed ----------------------------------------------------------------------------------

    [Fact]
    public async Task ARenewedClientCertificateStaysTrustedThroughItsCa()
    {
        var ct = TestContext.Current.CancellationToken;
        var ca = ApplicationCertificate.CreateAuthority("Plant CA", "CICTNA RDC");
        var serverStore = Store("server");
        serverStore.Trust(ca.Certificate); // the server trusts the plant CA, not individual clients
        await using var server = await StartServerAsync(serverStore);

        var clientStore = Store("client");
        clientStore.Trust(server.ServerCertificate!);
        // The client's current certificate, issued by the CA, expires in 5 days.
        var expiring = ApplicationCertificate.Create(ClientUri, "Trust Test Client", issuer: ca, validity: TimeSpan.FromDays(5));
        clientStore.SetApplicationCertificate(expiring);
        var own = new OwnCertificateSettings(ClientUri, "Trust Test Client") { Issuer = ca, RenewBefore = TimeSpan.FromDays(30) };

        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore) with { OwnCertificate = own });
        await client.ConnectAsync(server.LocalEndpointUrl, ct); // renews before connecting
        await ReadSomethingAsync(client);

        var current = clientStore.GetOrCreateApplicationCertificate(own, out var renewedAgain);
        Assert.False(renewedAgain);
        Assert.NotEqual(expiring.Thumbprint, current.Thumbprint);
        Assert.True(current.NotAfter > DateTime.UtcNow.AddDays(365));
        Assert.Contains(Directory.EnumerateFiles(clientStore.OwnOldPath), f => f.Contains(expiring.Thumbprint, StringComparison.Ordinal));
        Assert.Empty(serverStore.Rejected); // nothing had to be trusted again
    }

    [Fact]
    public async Task ARenewedSelfSignedServerCertificateMustBeTrustedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverStore = Store("server");
        var clientStore = Store("client");
        serverStore.Trust(ClientCertificate(clientStore).Certificate);
        var own = new OwnCertificateSettings(ServerUri, "Trust Test Server") { DnsNames = ["localhost"], RenewBefore = TimeSpan.FromDays(30) };
        serverStore.SetApplicationCertificate(ApplicationCertificate.Create(ServerUri, "Trust Test Server", dnsNames: ["localhost"], validity: TimeSpan.FromDays(3)));
        var old = serverStore.GetOrCreateApplicationCertificate(own with { RenewBefore = TimeSpan.Zero });
        clientStore.Trust(old.Certificate);

        // At start the server renews its certificate (it expires within the renewal window).
        await using var server = await StartServerAsync(serverStore, own: own);
        var renewed = new ApplicationCertificate(server.ServerCertificate!, []);
        Assert.NotEqual(old.Thumbprint, renewed.Thumbprint);

        // Self-signed: the client trusted the old certificate only, so it refuses the new one
        // until an operator trusts it. (With a CA, as in the test above, nothing has to change.)
        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore));
        var refused = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.LocalEndpointUrl, ct));
        Assert.Equal(StatusCodes.BadCertificateUntrusted, refused.StatusCode);
        Assert.True(clientStore.TrustRejected(renewed.Thumbprint));
        await client.ConnectAsync(server.LocalEndpointUrl, ct);
        await ReadSomethingAsync(client);
    }

    // ---- CA chains and revocation -------------------------------------------------------------------

    [Fact]
    public async Task CertificatesFromATrustedCaAreAcceptedOnBothSides()
    {
        var ct = TestContext.Current.CancellationToken;
        var ca = ApplicationCertificate.CreateAuthority("Plant CA");
        var serverStore = Store("server");
        var clientStore = Store("client");
        serverStore.Trust(ca.Certificate);
        clientStore.Trust(ca.Certificate);
        var own = new OwnCertificateSettings(ServerUri, "Trust Test Server") { DnsNames = ["localhost"], Issuer = ca };
        await using var server = await StartServerAsync(serverStore, own: own);
        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore) with
        {
            OwnCertificate = new OwnCertificateSettings(ClientUri, "Trust Test Client") { Issuer = ca },
        });
        await client.ConnectAsync(server.LocalEndpointUrl, ct);
        await ReadSomethingAsync(client);
    }

    [Fact]
    public async Task AnIssuerCaIsUsedForTheChainButNotTrustedByItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var ca = ApplicationCertificate.CreateAuthority("Plant CA");
        var serverCertificate = ApplicationCertificate.Create(ServerUri, "Trust Test Server", dnsNames: ["localhost"], issuer: ca);
        await using var server = await StartServerAsync(certificate: serverCertificate, acceptAnyClient: true);
        var clientStore = Store("client");
        clientStore.AddIssuer(ca.Certificate); // known, but not trusted

        await using var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore));
        var refused = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.LocalEndpointUrl, ct));
        Assert.Equal(StatusCodes.BadCertificateUntrusted, refused.StatusCode);

        // Trusting this one server certificate is enough: the issuer completes its chain.
        clientStore.Trust(serverCertificate.Certificate);
        await client.ConnectAsync(server.LocalEndpointUrl, ct);
        await ReadSomethingAsync(client);
    }

    [Fact]
    public async Task ARevokedServerCertificateIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var ca = ApplicationCertificate.CreateAuthority("Plant CA");
        var serverCertificate = ApplicationCertificate.Create(ServerUri, "Trust Test Server", dnsNames: ["localhost"], issuer: ca);
        await using var server = await StartServerAsync(certificate: serverCertificate, acceptAnyClient: true);
        var clientStore = Store("client");
        clientStore.Trust(ca.Certificate);

        // An empty CRL from the CA: nothing revoked, the certificate is accepted.
        clientStore.AddRevocationList(ca.CreateRevocationList([], 1));
        await using (var client = await OpcUaClient.CreateAsync(ClientOptions(clientStore)))
        {
            await client.ConnectAsync(server.LocalEndpointUrl, ct);
        }

        // A newer CRL revokes the server certificate.
        foreach (var file in Directory.EnumerateFiles(clientStore.TrustedCrlPath))
        {
            File.Delete(file);
        }
        clientStore.AddRevocationList(ca.CreateRevocationList([serverCertificate.Certificate], 2));
        await using var refusedClient = await OpcUaClient.CreateAsync(ClientOptions(clientStore));
        var refused = await Assert.ThrowsAsync<OpcUaException>(() => refusedClient.ConnectAsync(server.LocalEndpointUrl, ct));
        Assert.Equal(StatusCodes.BadCertificateRevoked, refused.StatusCode);
    }

    [Fact]
    public async Task ACaCertificateCannotBeUsedAsAnApplicationCertificate()
    {
        var ct = TestContext.Current.CancellationToken;
        var ca = ApplicationCertificate.CreateAuthority("Not An Application");
        var clientStore = Store("client");
        clientStore.Trust(ca.Certificate);
        Assert.Equal(StatusCodes.BadCertificateUseNotAllowed, clientStore.ReadTrustList().Verify(ca.Certificate));
        await Task.CompletedTask.WaitAsync(ct);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < Generous, $"timed out waiting for {what}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The server applies trust list changes within its refresh interval; retry until then.</summary>
    private static async Task RetryConnectAsync(OpcUaClient client, string url)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await client.ConnectAsync(url, TestContext.Current.CancellationToken);
                return;
            }
            catch (OpcUaException) when (deadline.Elapsed < Generous)
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
            }
        }
    }
}
