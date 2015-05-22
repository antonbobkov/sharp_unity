using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ServerClient
{
    class SocketListener
    {
        Action<Socket> processConnection;
        Socket sckListen;

        public SocketListener(Action<Socket> processConnection_, Socket sckListen_)
        {
            processConnection = processConnection_;
            sckListen = sckListen_;
            new Thread(() => this.ProcessThread()).Start();
        }

        void ProcessThread()
        {
            while (true)
                processConnection(sckListen.Accept());
        }
    }

    class ConnectionProcessor
    {
        public static Action<Socket> ProcessWithHandshake(Action<Handshake, Socket> processConnection)
        {
            return (sck) => ConnectionHandshake(sck, processConnection);
        }
        
        public static void ConnectionHandshake(Socket sckRead, Action<Handshake, Socket> processConnection)
        {
            using (NetworkStream connectionStream = new NetworkStream(sckRead, false))
            {
                int nTp = connectionStream.ReadByte();

                if (nTp != (int)MessageType.HANDSHAKE)
                {
                    Console.WriteLine("Invalid incoming connection message (expecting handshake): type {0} {1}", nTp, (MessageType) nTp);
                    return;
                }

                Handshake their_info = (Handshake)SocketReader.ReadSerializedMessage(connectionStream);

                processConnection(their_info, sckRead);
            }
        }
    }
}
