using System.Net.Sockets;

namespace LocalProjectBridge.Core.Processes;

/// <summary>端口由系统动态分配（设计文档 6.6），避免固定端口冲突。</summary>
public static class TcpPortAllocator
{
    public static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally { listener.Stop(); }
    }
}
