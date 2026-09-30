using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>Extra trusted roots (private CA) for the PRFactory HttpClient, on top of the system store.</summary>
public static class PRFactoryTrust
{
    /// <summary>Parses a PEM file holding one or more certificates; throws when unreadable or empty.</summary>
    public static X509Certificate2Collection LoadCa(string path)
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(path);
        if (roots.Count == 0)
        {
            throw new FormatException("no certificates found in CA file");
        }
        return roots;
    }

    public static SocketsHttpHandler CreateHandler(string caFile)
    {
        var roots = LoadCa(caFile);
        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => Validate(roots, certificate, errors)
            }
        };
    }

    /// <summary>System validation wins; only a pure chain-trust failure is retried against the extra roots.</summary>
    public static bool Validate(X509Certificate2Collection roots, System.Security.Cryptography.X509Certificates.X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }
        // Name mismatch or a missing certificate is never excused by an extra root.
        if (certificate is null || errors != SslPolicyErrors.RemoteCertificateChainErrors)
        {
            return false;
        }
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(leaf))
        {
            return false; // Expired, untrusted or otherwise invalid.
        }
        return chain.ChainElements.Count > 0 && roots.Any(root => root.Thumbprint == chain.ChainElements[^1].Certificate.Thumbprint);
    }
}
