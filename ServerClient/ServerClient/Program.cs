using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Client
{
    class Program
    {
        static void Main(string[] args)
        {
            IPHostEntry hostDnsEntry = Dns.GetHostEntry("localhost");
            IPAddress serverIp = hostDnsEntry.AddressList[0];

            Console.WriteLine(serverIp);

            while (true)
            {
                Socket daytimeSocket = new Socket(
                    serverIp.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);

                daytimeSocket.Connect(serverIp, 13);
                string data;
                using (Stream timeServiceStream = new NetworkStream(daytimeSocket, true))
                using (StreamReader timeServiceReader = new StreamReader(timeServiceStream))
                {
                    data = timeServiceReader.ReadToEnd();
                }

                Console.WriteLine("{0}: {1}", data.Length, data);

                daytimeSocket.Dispose();
            }
        }
    }
}
