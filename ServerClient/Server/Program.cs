using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Server
{
    class Program
    {
        static string sMessage = "Hello";

        static void ServerRun(string sIpAddr, int nPort)
        {
            Console.WriteLine("ServerRun at {0}:{1}", sIpAddr, nPort);

            try
            {
                using (Socket daytimeListener = new Socket(
                    AddressFamily.InterNetworkV6,
                    SocketType.Stream,
                    ProtocolType.Tcp))
                {
                    daytimeListener.SetSocketOption(SocketOptionLevel.IP,
                                                    (SocketOptionName)27, 0);

                    IPEndPoint daytimeEndpoint = new IPEndPoint(IPAddress.Parse(sIpAddr), nPort);
                    daytimeListener.Bind(daytimeEndpoint);
                    daytimeListener.Listen(20);

                    while (true)
                    {
                        Socket incomingConnection = daytimeListener.Accept();
                        using (NetworkStream connectionStream =
                                            new NetworkStream(incomingConnection, true))
                        {
                            using (StreamWriter writer = new StreamWriter(connectionStream,
                                                                            Encoding.ASCII))
                            {
                                //Client.MessageType m = Client.MessageType.HANDSHAKE;

                                for (int i = 0; i < 5; ++i)
                                {
                                    connectionStream.WriteByte((byte)ServerClient.MessageType.HANDSHAKE);
                                    byte[] msg = System.Text.Encoding.Default.GetBytes("12345678");
                                    connectionStream.Write(msg, 0, msg.Length);

                                    connectionStream.Flush();
                                    Thread.Sleep(1000);
                                }
                            }
                        }
                    }
                }
            }
            catch (ThreadAbortException)
            {
                Console.WriteLine("ServerRun at {0}:{1} exits gracefully", sIpAddr, nPort);
            }
        }

        static void Main(string[] args)
        {
            string sIpAddr = "127.0.0.1";
            int nPort = 13;

            Thread tServer = new Thread(() => ServerRun(sIpAddr, nPort));

            tServer.Start();

            while (true)
            {
                string sInput = Console.ReadLine();
                var param = new LinkedList<string> (sInput.Split(' '));
                string sCommand = param.FirstOrDefault();

                if ("message".StartsWith(sCommand))
                {
                    param.RemoveFirst();
                    lock (sMessage)
                        sMessage = string.Join(" ", param.ToArray());
                }
                else
                    Console.WriteLine("Unknown command");
            }
        }
    }
}
