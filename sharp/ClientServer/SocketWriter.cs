using ServerClient.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Xml.Serialization;

namespace ServerClient
{
    class SocketWriter
    {
        Socket socketWrite;
        BlockingCollection<Action<Stream>> bcMessages = new BlockingCollection<Action<Stream>> ();
        Action<IOException> errorResponse;

        public bool CanWrite() { return socketWrite != null; }

        public void StartWriting(Socket socketWrite_, Action<IOException> errorResponse_)
        {
            if (CanWrite())
                throw new InvalidOperationException("SocketWriter's socketWrite already initialized");

            socketWrite = socketWrite_;
            errorResponse = errorResponse_;
            new Thread(() => this.ProcessThread()).Start();
        }

        public void SendMessage<T>(MessageType mt, T message)
        {
            MemoryStream ms = new MemoryStream();
            StreamSerializedMessage(ms, mt, message);
            ms.Position = 0;

            bcMessages.Add(stm => Serializer.SendStream(stm, ms));
        }

        void ProcessThread()
        {
            try
            {
                using (NetworkStream connectionStream = new NetworkStream(socketWrite, true))
                    while (true)
                    {
                        bcMessages.Take().Invoke(connectionStream);
                        connectionStream.Flush();
                    }
            }
            catch (IOException ioe)
            {
                errorResponse(ioe);
            }
        }

        public static void StreamSerializedMessage<T>(Stream stm, MessageType mt, T message)
        {
            stm.WriteByte((byte)mt);

            if (message != null)
                Serializer.Serialize(stm, message);
        }
    }
}
