using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Server
{
    class Program
    {
        static void Main(string[] args)
        {
            using (Socket daytimeListener = new Socket(
                AddressFamily.InterNetworkV6,
                SocketType.Stream,
                ProtocolType.Tcp))
            {
                daytimeListener.SetSocketOption(SocketOptionLevel.IPv6,
                                                (SocketOptionName)27, 0);

                IPEndPoint daytimeEndpoint = new IPEndPoint(IPAddress.IPv6Any, 13);
                daytimeListener.Bind(daytimeEndpoint);
                daytimeListener.Listen(20);

                while (true)
                {
                    Socket incomingConnection = daytimeListener.Accept();
                    using (NetworkStream connectionStream =
                                        new NetworkStream(incomingConnection, true))
                    using (StreamWriter writer = new StreamWriter(connectionStream,
                                                                    Encoding.ASCII))
                    {
                        writer.WriteLine(DateTime.Now);
                    }
                }
            }
        }
    }
}
