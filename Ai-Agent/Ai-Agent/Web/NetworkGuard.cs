using System.Net;
using System.Net.Sockets;

namespace Ai_Agent.Web
{
    public class WebOptions
    {
        /// <summary>web_fetch: "ask" (each fetch shows the URL for approval), "allow" (no prompt), "off" (tool not offered).</summary>
        public string Fetch { get; set; } = "ask";

        /// <summary>web_search provider: "brave", "tavily", "searxng", or empty (tool not offered).</summary>
        public string? SearchProvider { get; set; }
        public string? SearchApiKey { get; set; }

        /// <summary>SearXNG instance, e.g. http://localhost:8080 (the user's own server, so private addresses are fine).</summary>
        public string? SearxngUrl { get; set; }

        public int TimeoutSeconds { get; set; } = 20;
        public int MaxBytes { get; set; } = 2 * 1024 * 1024;

        /// <summary>Characters of page text per web_fetch call (longer pages continue with "start").</summary>
        public int MaxChars { get; set; } = 12_000;

        /// <summary>Tests only: lets web_fetch reach local test servers.</summary>
        public bool AllowPrivateHosts { get; set; }
    }

    /// <summary>
    /// SSRF protection for web_fetch. The backend runs on the user's machine, so a fetch must never reach it,
    /// the local network or cloud metadata. The check runs when the socket CONNECTS, on the address actually
    /// used, so a public name that resolves to 127.0.0.1 (or changes between lookups) is refused too, and so is
    /// every redirect hop.
    /// </summary>
    public static class NetworkGuard
    {
        public static HttpClient CreateClient(WebOptions options)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,               // redirects are followed by the caller, one checked hop at a time
                UseProxy = false,                         // a proxy would connect for us and skip the address check
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = (context, cancellationToken) => ConnectAsync(context, options.AllowPrivateHosts, cancellationToken),
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool allowPrivate, CancellationToken cancellationToken)
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, cancellationToken);
            var usable = addresses.Where(a => allowPrivate || IsPublic(a)).ToArray();
            if (usable.Length == 0)
                throw new BlockedAddressException(host);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(usable, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>True only for globally routable unicast addresses.</summary>
        public static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                return !(b[0] == 0                                          // "this network"
                      || b[0] == 10                                         // private
                      || b[0] == 100 && b[1] >= 64 && b[1] <= 127           // carrier-grade NAT
                      || b[0] == 127                                        // loopback
                      || b[0] == 169 && b[1] == 254                         // link-local, cloud metadata
                      || b[0] == 172 && b[1] >= 16 && b[1] <= 31            // private
                      || b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2) // IETF, TEST-NET-1
                      || b[0] == 192 && b[1] == 168                         // private
                      || b[0] == 198 && (b[1] == 18 || b[1] == 19)          // benchmarking
                      || b[0] == 198 && b[1] == 51 && b[2] == 100           // TEST-NET-2
                      || b[0] == 203 && b[1] == 0 && b[2] == 113            // TEST-NET-3
                      || b[0] >= 224);                                      // multicast, reserved, broadcast
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address)) return false;
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
                var b = address.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return false;                    // fc00::/7 unique local
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;   // 2001:db8::/32 documentation
                if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)                 // 64:ff9b::/96 NAT64: judge the IPv4 inside
                    return IsPublic(new IPAddress(b[12..16]));
                return true;
            }

            return false;
        }
    }

    public sealed class BlockedAddressException : HttpRequestException
    {
        public BlockedAddressException(string host)
            : base($"'{host}' is a local or private network address. web_fetch only opens public websites.") { }
    }
}
