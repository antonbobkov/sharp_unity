using ServerClient.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Xml.Serialization;

namespace ServerClient
{
    class SocketWriterMessage
    {
        public MessageType mt;
        public MemoryStream message;
    }

    class SocketWriter
    {
        public SocketWriter(IPEndPoint address, Action<Exception, DisconnectType> errorResponse, Handshake info)
        {
            this.address = address;
            this.errorResponse = errorResponse;
            this.info = info;

            ThreadManager.NewThread(WritingThread, TerminateThread,
                "SocketWriter " + address);
        }

        public void SendMessage(SocketWriterMessage swm)
        {
            bcMessages.Add(swm);
        }
        public void TerminateThread()
        {
            bcMessages.Add(null);
        }

        public static SocketWriterMessage SerializeMessage(MessageType mt, params object[] messages)
        {
            MemoryStream ms = new MemoryStream();

            Serializer.Serialize(ms, messages);

            ms.Position = 0;

            return new SocketWriterMessage { mt = mt, message = ms };
        }

        private void WritingThread()
        {
            using (Socket writeSocket = new Socket(
                    address.AddressFamily,
                    SocketType.Stream,
                    ProtocolType.Tcp))
            {
                try
                {
                    writeSocket.Connect(address);//, TimeSpan.FromSeconds(5));

                    // send handshake data
                    using (NetworkStream connectionStream = new NetworkStream(writeSocket, false))
                    {
                        SocketWriter.SendNetworkMessage(connectionStream, SocketWriter.SerializeMessage(MessageType.HANDSHAKE, info));

                        if (info.hasExtraInfo)
                        {
                            info.ExtraInfo.Position = 0;
                            Serializer.SendStream(connectionStream, info.ExtraInfo);
                        }
                    }

                }
                catch (Exception e)
                {
                    errorResponse(e, DisconnectType.WRITE_CONNECT_FAIL);
                    return;
                }

                try
                {
                    using (NetworkStream connectionStream = new NetworkStream(writeSocket, false))
                        while (true)
                        {
                            SocketWriterMessage act = bcMessages.Take();
                            if (act == null)
                            {
                                //Log.LogWriteLine("SocketWriter terminated gracefully");
                                return;
                            }

                            SendNetworkMessage(connectionStream, act);
                        }
                }
                catch (IOException ioe)
                {
                    errorResponse(ioe, DisconnectType.WRITE);
                }

            }
        }
        private static void SendNetworkMessage(NetworkStream connectionStream, SocketWriterMessage swm)
        {
            connectionStream.WriteByte((byte)swm.mt);

            swm.message.Position = 0;
            Serializer.SendStream(connectionStream, swm.message);
        }

        private IPEndPoint address;
        private BlockingCollection<SocketWriterMessage> bcMessages = new BlockingCollection<SocketWriterMessage>();
        private Action<Exception, DisconnectType> errorResponse;
        private Handshake info;

    }
}
