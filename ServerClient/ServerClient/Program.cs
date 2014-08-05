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
            IPHostEntry hostDnsEntry = Dns.GetHostEntry(Dns.GetHostName());
            IPHostEntry localDnsEntry = Dns.GetHostEntry("localhost");

            var hostIPs = Dns.GetHostEntry(Dns.GetHostName()).AddressList.Select(x => x);
            var localIPs = Dns.GetHostEntry("localhost").AddressList.Select(x => x);
            var extraIPs = new IPAddress[] { IPAddress.Parse("192.168.1.66") }.Select(x => x);

            var allIPs = hostIPs.Concat(localIPs).Concat(extraIPs);


            foreach (IPAddress address in allIPs)
            {
                Console.WriteLine("Type: {0}, Address: {1}", address.AddressFamily,
                                                             address);
            }

            Console.WriteLine();

            foreach (IPAddress address in allIPs)
            {
                Console.WriteLine("Trying {0} {1}", address.AddressFamily,
                                                             address);
                Socket daytimeSocket = new Socket(
                    address.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp);

                try
                {
                    daytimeSocket.Connect(address, 13);
                    string data;
                    using (Stream timeServiceStream = new NetworkStream(daytimeSocket, true))
                    using (StreamReader timeServiceReader = new StreamReader(timeServiceStream))
                    {
                        data = timeServiceReader.ReadToEnd();
                    }

                    Console.WriteLine("{0}: {1}", data.Length, data);

                }
                catch( Exception )
                {
                    Console.WriteLine("Failed");
//                    Console.WriteLine(ex.ToString());
                }

                Console.WriteLine();
                daytimeSocket.Dispose();
            }
        }
    }
}
