using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Features.PRFactory;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class CaFileTests
{
    static (X509Certificate2 Ca, X509Certificate2 Leaf) Issue(string host = "localhost", bool expired = false)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(30));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest($"CN={host}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var from = expired ? DateTimeOffset.UtcNow.AddDays(-20) : DateTimeOffset.UtcNow.AddDays(-1);
        var to = expired ? DateTimeOffset.UtcNow.AddDays(-10) : DateTimeOffset.UtcNow.AddDays(10);
        using var signed = leafRequest.Create(ca, from, to, RandomNumberGenerator.GetBytes(8));
        var leaf = signed.CopyWithPrivateKey(leafKey);
        return (ca, X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pfx), null));
    }

    static string WritePem(string dir, X509Certificate2 ca)
    {
        var path = Path.Combine(dir, "ca.pem");
        File.WriteAllText(path, ca.ExportCertificatePem());
        return path;
    }

    [Fact]
    public void Connect_stores_the_absolute_ca_path_and_reconnect_without_it_clears_it()
    {
        using var dir = new TempStateDir();
        var state = StateDirectory.Open(dir.Path);
        var (ca, _) = Issue();
        var pem = WritePem(dir.Path, ca);
        var options = new Dictionary<string, string> { ["url"] = "https://prfactory.test", ["token-scope"] = "tenant-wide", ["ca-file"] = pem };
        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", options, [], new StringReader("t")));
        Assert.Equal(pem, PRFactoryConnection.LoadSettings(state)!.CaFile);

        options.Remove("ca-file");
        Assert.Equal(0, PRFactoryConnection.Run(state, "connect", options, [], new StringReader("t")));
        Assert.Null(PRFactoryConnection.LoadSettings(state)!.CaFile);

        File.WriteAllText(pem, "not a certificate");
        options["ca-file"] = pem;
        Assert.Equal(64, PRFactoryConnection.Run(state, "connect", options, [], new StringReader("t")));
        Assert.Null(PRFactoryConnection.LoadSettings(state)!.CaFile);
    }

    [Fact]
    public async Task Private_ca_server_is_accepted_with_the_ca_file_and_rejected_without_it()
    {
        using var dir = new TempStateDir();
        var (ca, leaf) = Issue();
        var pem = WritePem(dir.Path, ca);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                using var tcp = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                try
                {
                    await using var tls = new SslStream(tcp.GetStream());
                    await tls.AuthenticateAsServerAsync(leaf);
                    var buffer = new byte[4096];
                    _ = await tls.ReadAsync(buffer, TestContext.Current.CancellationToken);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), TestContext.Current.CancellationToken);
                    await tls.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { }
            }
        }, TestContext.Current.CancellationToken);

        var url = $"https://localhost:{port}/";
        using (var trusting = new HttpClient(PRFactoryTrust.CreateHandler(pem)))
        {
            Assert.Equal("ok", await trusting.GetStringAsync(url, TestContext.Current.CancellationToken));
        }
        using var plain = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => plain.GetStringAsync(url, TestContext.Current.CancellationToken));
        listener.Stop();
        try { await serving; } catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    [Fact]
    public void Extra_root_never_excuses_name_mismatch_or_expiry()
    {
        var (ca, leaf) = Issue();
        X509Certificate2Collection roots = [ca];
        Assert.True(PRFactoryTrust.Validate(roots, leaf, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(PRFactoryTrust.Validate(roots, leaf, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(PRFactoryTrust.Validate(roots, leaf, SslPolicyErrors.RemoteCertificateNameMismatch));

        var (expiredCa, expired) = Issue(expired: true);
        Assert.False(PRFactoryTrust.Validate([expiredCa], expired, SslPolicyErrors.RemoteCertificateChainErrors));
        var (otherCa, _) = Issue();
        Assert.False(PRFactoryTrust.Validate([otherCa], leaf, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
