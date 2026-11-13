using System.Net;
using System.Net.Sockets;

namespace InternetData;

/// <summary>The bogon check, without a client to hand.</summary>
/// <remarks>
/// C# cannot carry a static and an instance method of the same signature on one type, so the
/// standalone form needs its own class. <see cref="InternetDataClient.IsBogon"/> is the same check
/// on the client the README teaches, since a caller usually holds one already.
/// </remarks>
public static class Bogon
{
    /// <summary>
    /// Whether an address is a bogon: private, loopback, link-local, documentation, multicast or
    /// otherwise not routable on the public internet, including the IPv6 equivalents and the 6to4
    /// and Teredo ranges that wrap them.
    /// </summary>
    /// <remarks>
    /// No database describes them, so the client answers them itself and they never cost a
    /// request. Anything that is not an address at all is false: this is a classification, not a
    /// validator.
    /// </remarks>
    public static bool IsBogon(string ip)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return false;
        }
        ip = Unmapped(ip);
        // Routing on the colon rather than on the parsed family: what is left with one is IPv6,
        // the IPv4-compatible ::a.b.c.d included, which stays inside ::/96.
        if (ip.Contains(':', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(ip, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
            var addr6 = ToUInt128(v6);
            foreach (var range in V6)
            {
                if ((addr6 & range.Mask) == range.Net)
                {
                    return true;
                }
            }
            return false;
        }

        if (!IPAddress.TryParse(ip, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var addr4 = ToUInt32(v4);
        foreach (var range in V4)
        {
            if ((addr4 & range.Mask) == range.Net)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The IPv4 address an IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>, in any spelling)
    /// carries, and any other string as given.
    /// </summary>
    /// <remarks>
    /// A server listening on <c>::</c> sees every IPv4 visitor in that form, which read whole is
    /// inside <c>::ffff:0:0/96</c>, so judging it whole would answer every such visitor locally as a
    /// bogon. <c>::a.b.c.d</c> is IPv4-compatible rather than mapped, and stays IPv6.
    /// </remarks>
    internal static string Unmapped(string ip)
        => ip.Contains(':', StringComparison.Ordinal)
            && IPAddress.TryParse(ip, out var addr)
            && addr.IsIPv4MappedToIPv6
                ? addr.MapToIPv4().ToString()
                : ip;

    // Parsed on first use rather than at load: a consumer that never looks up an address should
    // not pay for the table.
    private static readonly Range32[] V4 = Bogons.V4.Select(ParseV4).ToArray();
    private static readonly Range128[] V6 = Bogons.V6.Select(ParseV6).ToArray();

    private readonly record struct Range32(uint Net, uint Mask);

    private readonly record struct Range128(UInt128 Net, UInt128 Mask);

    private static Range32 ParseV4(string cidr)
    {
        var (net, bits) = SplitCidr(cidr);
        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        return new Range32(ToUInt32(IPAddress.Parse(net)) & mask, mask);
    }

    private static Range128 ParseV6(string cidr)
    {
        var (net, bits) = SplitCidr(cidr);
        var mask = bits == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - bits);
        return new Range128(ToUInt128(IPAddress.Parse(net)) & mask, mask);
    }

    private static (string Net, int Bits) SplitCidr(string cidr)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        return (cidr[..slash], int.Parse(cidr[(slash + 1)..]));
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static UInt128 ToUInt128(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        UInt128 value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }
        return value;
    }
}
