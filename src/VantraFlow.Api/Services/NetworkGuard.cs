using System.Net;
using System.Net.Sockets;

namespace VantraFlow.Api.Services;

/// <summary>
/// SSRF guard for the HTTP request step (copied from Vantra Orbit): decides which addresses the
/// server may connect to on a user's behalf — public unicast only, never
/// loopback, private, link-local (incl. the 169.254.169.254 metadata service),
/// CGNAT, multicast, documentation or other reserved ranges, nor Azure's
/// platform address 168.63.129.16.
///
/// <see cref="ConnectAsync"/> is the webhook HttpClient's ConnectCallback, so
/// the check runs on the address actually being dialled, at connect time —
/// a hostname that resolved to something public during the up-front check and
/// to 127.0.0.1 a moment later (DNS rebinding) is still refused.
/// </summary>
public static class NetworkGuard
{
    /// <summary>True only for a public, globally routable unicast address.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(
                b[0] == 0                                        // 0.0.0.0/8 "this network"
                || b[0] == 10                                    // 10.0.0.0/8 private
                || (b[0] == 100 && (b[1] & 0xC0) == 64)          // 100.64.0.0/10 CGNAT
                || b[0] == 127                                   // 127.0.0.0/8 loopback
                || (b[0] == 169 && b[1] == 254)                  // 169.254.0.0/16 link-local / cloud metadata
                || (b[0] == 172 && (b[1] & 0xF0) == 16)          // 172.16.0.0/12 private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0.0/24 IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)       // 192.0.2.0/24 TEST-NET-1
                || (b[0] == 192 && b[1] == 88 && b[2] == 99)     // 192.88.99.0/24 6to4 relay anycast
                || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16 private
                || (b[0] == 198 && (b[1] & 0xFE) == 18)          // 198.18.0.0/15 benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)    // 198.51.100.0/24 TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)     // 203.0.113.0/24 TEST-NET-3
                || b[0] >= 224                                   // 224/4 multicast, 240/4 reserved, broadcast
                || (b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16)); // Azure WireServer
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            // Only global unicast (2000::/3) is allowed; that already excludes
            // ::, ::1, IPv4-compatible ::/96, fc00::/7 unique-local, fe80::/10
            // link-local, fec0::/10 site-local, ff00::/8 multicast and the
            // 64:ff9b::/96 NAT64 prefix.
            if ((b[0] & 0xE0) != 0x20) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32 documentation
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false; // 2001::/32 Teredo (tunnels to IPv4)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) == 0x10) return false; // 2001:10::/28 ORCHID
            if (b[0] == 0x20 && b[1] == 0x02)                                                         // 2002::/16 6to4: check the embedded IPv4
                return IsPublic(new IPAddress(new[] { b[2], b[3], b[4], b[5] }));
            return true;
        }

        return false;
    }

    /// <summary>
    /// SocketsHttpHandler.ConnectCallback: resolve the host, refuse unless EVERY
    /// address it resolves to is public, then connect to one of them.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await ResolveAsync(host, ct);
        if (addresses.Length == 0)
            throw new WebhookBlockedException($"Couldn't resolve {host}.");
        if (addresses.Any(a => !IsPublic(a)))
            throw new WebhookBlockedException("That address is on a private or reserved network, so Vantra Flow won't call it.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The addresses a host name (or IP literal) stands for.</summary>
    public static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var bare = host.Trim('[', ']');
        if (IPAddress.TryParse(bare, out var literal)) return [literal];
        try { return await Dns.GetHostAddressesAsync(bare, ct); }
        catch (SocketException) { return []; }
    }
}

/// <summary>A webhook target the guard refused (or couldn't resolve).</summary>
public sealed class WebhookBlockedException(string message) : Exception(message);
