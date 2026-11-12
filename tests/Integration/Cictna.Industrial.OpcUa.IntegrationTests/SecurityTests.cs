using System.Security.Cryptography.X509Certificates;
using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.IntegrationTests;

/// <summary>Server with a certificate, anonymous access off, and one user login.</summary>
public sealed class SecureTestServer : IAsyncLifetime
{
    public const string ServerUri = "urn:cictna:test:secure-server";
    public const string User = "operator";
    public const string Password = "Pump-01!";

    public OpcUaServer Server { get; private set; } = null!;
    public string Url => Server.LocalEndpointUrl;

    public async ValueTask InitializeAsync()
    {
        var certificate = ApplicationCertificate.CreateSelfSigned(ServerUri, "Cictna Test Server", "CICTNA RDC", "localhost");
        Server = await OpcUaServer.StartAsync(new OpcUaServerOptions
        {
            Port = TestServer.FreePort(),
            ApplicationUri = ServerUri,
            Certificate = certificate,
            AllowAnonymous = false,
            User = new UserIdentity.UserNameIdentity(User, Password),
        });
    }

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>
/// Secure channels (Sign, SignAndEncrypt with Basic256Sha256), user authentication and
/// certificate creation. Trust lists and certificate validation come in Phase 3; until then
/// secure connections in these tests use AcceptAnyServerCertificateForDevelopment.
/// </summary>
public sealed class SecurityTests(SecureTestServer server) : IClassFixture<SecureTestServer>
{
    private const string ClientUri = "urn:cictna:test:client";
    private static readonly ApplicationCertificate ClientCertificate =
        ApplicationCertificate.CreateSelfSigned(ClientUri, "Cictna Test Client");

    private static OpcUaClientOptions Secure(MessageSecurityMode mode, UserIdentity? identity = null) => new()
    {
        ApplicationUri = ClientUri,
        Certificate = ClientCertificate,
        SecurityMode = mode,
        SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
        AcceptAnyServerCertificateForDevelopment = true,
        Identity = identity ?? UserIdentity.UserName(SecureTestServer.User, SecureTestServer.Password),
    };

    [Fact]
    public void SelfSignedCertificatesCarryTheApplicationUri()
    {
        using var x509 = X509CertificateLoader.LoadCertificate(ClientCertificate.Certificate);
        Assert.Contains("CN=Cictna Test Client", x509.Subject, StringComparison.Ordinal);
        var san = x509.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains(ClientUri, san.Format(false), StringComparison.Ordinal);
        Assert.True(x509.NotAfter > DateTime.UtcNow.AddDays(300));
        Assert.Equal(2048, x509.GetRSAPublicKey()!.KeySize);
        Assert.NotEmpty(ClientCertificate.PrivateKey);
        Assert.DoesNotContain("PrivateKey", ClientCertificate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointsOfferNoneSignAndSignAndEncrypt()
    {
        var endpoints = await OpcUaClient.GetEndpointsAsync(server.Url, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(endpoints, e => e.SecurityMode == MessageSecurityMode.SignAndEncrypt && e.SecurityPolicyUri == SecurityPolicies.Basic256Sha256);
        Assert.Contains(endpoints, e => e.SecurityMode == MessageSecurityMode.Sign && e.SecurityPolicyUri == SecurityPolicies.Basic256Sha256);
        var secure = endpoints.First(e => e.SecurityMode == MessageSecurityMode.SignAndEncrypt);
        Assert.NotNull(secure.ServerCertificate);
        Assert.Equal(SecureTestServer.ServerUri, secure.Server.ApplicationUri);
        Assert.Contains(secure.UserIdentityTokens, t => t.TokenType == UserTokenType.UserName);
        Assert.DoesNotContain(secure.UserIdentityTokens, t => t.TokenType == UserTokenType.Anonymous);
    }

    [Theory]
    [InlineData(MessageSecurityMode.SignAndEncrypt)]
    [InlineData(MessageSecurityMode.Sign)]
    public async Task SecureChannelWithUserLoginWorks(MessageSecurityMode mode)
    {
        await using var client = await OpcUaClient.CreateAsync(Secure(mode));
        await client.ConnectAsync(server.Url, TestContext.Current.CancellationToken);
        var time = await client.ReadAsync(WellKnownNodes.ServerCurrentTime, TestContext.Current.CancellationToken);
        Assert.True(time.Status.IsGood);
        Assert.True((DateTime.UtcNow - time.Value.GetValue<DateTime>()).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task WrongPasswordIsRejected()
    {
        await using var client = await OpcUaClient.CreateAsync(Secure(MessageSecurityMode.SignAndEncrypt, UserIdentity.UserName(SecureTestServer.User, "wrong")));
        var ex = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.Url, TestContext.Current.CancellationToken));
        Assert.True(ex.StatusCode.IsBad);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task AnonymousIsRejectedWhenTheServerRequiresAUser()
    {
        await using var client = await OpcUaClient.CreateAsync(Secure(MessageSecurityMode.SignAndEncrypt, UserIdentity.Anonymous));
        var ex = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.Url, TestContext.Current.CancellationToken));
        Assert.True(ex.StatusCode.IsBad);
    }

    [Fact]
    public async Task SecureModeWithoutAClientCertificateFails()
    {
        var options = Secure(MessageSecurityMode.SignAndEncrypt) with { Certificate = null };
        await using var client = await OpcUaClient.CreateAsync(options);
        await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.Url, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UntrustedServerCertificateIsRejectedByDefault()
    {
        // Without the development flag and without a trust list (Phase 3), the self-signed server
        // certificate must not be accepted: secure by default.
        var options = Secure(MessageSecurityMode.SignAndEncrypt) with { AcceptAnyServerCertificateForDevelopment = false };
        await using var client = await OpcUaClient.CreateAsync(options);
        var ex = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(server.Url, TestContext.Current.CancellationToken));
        Assert.True(ex.StatusCode.IsBad);
    }

    [Fact]
    public async Task PasswordOverAnUnencryptedChannelStillWorksBecauseTheTokenIsEncrypted()
    {
        // SecurityMode None, but the server's user token policy uses Basic256Sha256, so open62541
        // encrypts the password with the server certificate even on a plain channel.
        var options = Secure(MessageSecurityMode.None) with { SecurityPolicyUri = SecurityPolicies.None };
        await using var client = await OpcUaClient.CreateAsync(options);
        await client.ConnectAsync(server.Url, TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task EveryTypeRoundTripsOverAnEncryptedChannel()
    {
        // Encryption changes message sizes and padding: check that values still survive.
        await using var plain = new TestServer
        {
            Options = port => new OpcUaServerOptions
            {
                Port = port,
                ApplicationUri = "urn:cictna:test:secure-types",
                Certificate = ApplicationCertificate.CreateSelfSigned("urn:cictna:test:secure-types", "Types"),
            },
        };
        await plain.InitializeAsync();
        var options = Secure(MessageSecurityMode.SignAndEncrypt, UserIdentity.Anonymous);
        await using var client = await OpcUaClient.CreateAsync(options);
        await client.ConnectAsync(plain.Url, TestContext.Current.CancellationToken);
        var names = plain.Variables.Keys.ToArray();
        var values = await client.ReadAsync(names.Select(n => new ReadValueId(plain.Variables[n].Node)).ToArray(),
            cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(plain.Variables[names[i]].Initial, values[i].Value);
        }
        var big = Variant.FromArray(Enumerable.Range(0, 20_000).Select(i => (double)i).ToArray());
        var node = await plain.Server.AddVariableAsync(plain.TypesFolder, new NodeId("Big", plain.Ns), "Big", big);
        Assert.Equal(big, (await client.ReadAsync(node, TestContext.Current.CancellationToken)).Value);
    }
}
