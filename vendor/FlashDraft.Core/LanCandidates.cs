using System.Net;
using System.Net.Sockets;

namespace FlashDraft.Transport;

/// <summary>Collects IPv4 endpoints that a peer on the same LAN can dial.</summary>
public static class LanCandidates
{
    public static IReadOnlyList<string> Collect(int port, bool includeLoopback = false)
    {
        var result = new List<string>();
        try
        {
            foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                byte[] bytes = address.GetAddressBytes();
                bool loopback = bytes[0] == 127;
                bool linkLocal = bytes[0] == 169 && bytes[1] == 254;
                if ((loopback && !includeLoopback) || linkLocal)
                {
                    continue;
                }

                string candidate = address + ":" + port;
                if (!result.Contains(candidate))
                {
                    result.Add(candidate);
                }
            }
        }
        catch (SocketException)
        {
            // Host name resolution is best effort.
        }

        if (result.Count == 0)
        {
            result.Add("127.0.0.1:" + port);
        }

        return result;
    }
}
