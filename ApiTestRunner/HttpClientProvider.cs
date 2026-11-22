using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiTestRunner;

/// <summary>
/// One HttpClient per client-certificate setting, so services using two-way SSL (mutual TLS)
/// present their certificate while JWT/plain services share a normal client.
///
/// Client Certificate (Environments sheet) can be:
///   certs\regtest-client.pfx          .pfx/.p12 file (relative to the workbook) + Certificate Password
///   thumbprint:3A1B...                certificate already installed in the Windows certificate store
///                                     (Current User → Personal, then Local Machine → Personal)
/// </summary>
public sealed class HttpClientProvider : IDisposable
{
    private readonly TestSuite _suite;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _timeout;
    private readonly bool _ignoreSsl;

    public HttpClientProvider(TestSuite suite)
    {
        _suite = suite;
        _timeout = TimeSpan.FromSeconds(suite.SettingInt("TimeoutSeconds", 60));
        _ignoreSsl = suite.SettingBool("IgnoreSslErrors", false);
    }

    public TimeSpan Timeout => _timeout;

    public HttpClient Get(string? certificate, string? password)
    {
        var key = string.IsNullOrWhiteSpace(certificate) ? "" : certificate.Trim();
        if (_clients.TryGetValue(key, out var existing)) return existing;

        lock (_clients)
        {
            if (_clients.TryGetValue(key, out existing)) return existing;

            var handler = new SocketsHttpHandler
            {
                // Cookies are NOT kept automatically: every cookie a step sends must be visible in the
                // Headers sheet (e.g. Cookie: access_token={{accessToken}}), so runs are predictable.
                UseCookies = false,
                // Long runs: recycle pooled connections so DNS / load-balancer changes are picked up,
                // and drop idle ones instead of reusing a connection the server already closed.
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
                MaxConnectionsPerServer = Math.Max(2, _suite.SettingInt("MaxParallel", 1) * 2),
                ConnectTimeout = TimeSpan.FromSeconds(Math.Min(30, _timeout.TotalSeconds)),
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };
            if (_ignoreSsl)
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            if (key.Length > 0)
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { LoadCertificate(key, password) };

            var client = new HttpClient(handler) { Timeout = _timeout };
            _clients[key] = client;
            return client;
        }
    }

    private X509Certificate2 LoadCertificate(string spec, string? password)
    {
        X509Certificate2 cert;
        if (spec.StartsWith("thumbprint:", StringComparison.OrdinalIgnoreCase))
        {
            var thumb = new string(spec[11..].Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            cert = FindInStore(thumb, StoreLocation.CurrentUser) ?? FindInStore(thumb, StoreLocation.LocalMachine)
                ?? throw new StepConfigException($"Client certificate with thumbprint {thumb} was not found in the Windows certificate store (Current User or Local Machine → Personal).");
        }
        else
        {
            var path = Path.IsPathRooted(spec) ? spec : Path.Combine(_suite.WorkbookFolder, spec);
            if (!File.Exists(path)) throw new StepConfigException($"Client certificate file not found: {path}");
            try
            {
                cert = new X509Certificate2(path, password, X509KeyStorageFlags.UserKeySet);
            }
            catch (CryptographicException ex)
            {
                throw new StepConfigException($"Could not open client certificate {Path.GetFileName(path)} - wrong Certificate Password? ({ex.Message})");
            }
        }

        if (!cert.HasPrivateKey)
            throw new StepConfigException($"Client certificate '{cert.Subject}' has no private key, so it cannot be used for two-way SSL. Use the .pfx/.p12 (not .cer/.crt).");
        if (DateTime.Now > cert.NotAfter)
            throw new StepConfigException($"Client certificate '{cert.Subject}' expired on {cert.NotAfter:yyyy-MM-dd}.");
        return cert;
    }

    private static X509Certificate2? FindInStore(string thumbprint, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).FirstOrDefault();
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
    }
}
