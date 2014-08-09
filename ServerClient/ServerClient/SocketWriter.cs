using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;

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

        public void SendMessage(MessageType mt, object message)
        {
            MemoryStream ms = new MemoryStream();
            StreamSerializedMessage(ms, mt, message);
            ms.Position = 0;

            bcMessages.Add(stm => SendStream(stm, ms));
        }

        void ProcessThread()
        {
            try
            {
                using (NetworkStream connectionStream = new NetworkStream(socketWrite, true))
                    while (true)
                        bcMessages.Take().Invoke(connectionStream);
            }
            catch (IOException ioe)
            {
                errorResponse(ioe);
            }
        }

        static void Serialize(Stream stm, object obj)
        {
            IFormatter formatter = new BinaryFormatter();
            formatter.Serialize(stm, obj);
        }

        static void StreamSerializedMessage(Stream stm, MessageType mt, object message)
        {
            stm.WriteByte((byte)mt);

            if(message != null)
                Serialize(stm, message);
        }

        static void SendStream(Stream network, Stream message)
        {
            message.CopyTo(network);
        }
    }
}
