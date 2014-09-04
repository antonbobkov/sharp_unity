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
            try
            {
                if (CanWrite())
                    throw new InvalidOperationException("SocketWriter's socketWrite already initialized");

                socketWrite = socketWrite_;
                errorResponse = errorResponse_;
                new Thread(() => this.ProcessThread()).Start();
            }
            catch (Exception)
            {
                socketWrite_.Close();
                throw;
            }
        }
        
        public void SendMessage(MessageType mt, params Object[] messages)
        {
            MemoryStream ms = new MemoryStream();

            ms.WriteByte((byte)mt);

            foreach(Object m in messages)
                Serializer.Serialize(ms, m);

            ms.Position = 0;

            bcMessages.Add(stm => Serializer.SendStream(stm, ms));
        }

        public void TerminateThread()
        {
            bcMessages.Add(null);
        }

        void ProcessThread()
        {
            try
            {
                using (NetworkStream connectionStream = new NetworkStream(socketWrite, true))
                    while (true)
                    {
                        var act = bcMessages.Take();
                        if (act == null)
                        {
                            //Log.LogWriteLine("SocketWriter terminated gracefully");
                            return;
                        }
                        act.Invoke(connectionStream);
                        //connectionStream.Flush();
                    }
            }
            catch (IOException ioe)
            {
                errorResponse(ioe);
            }
        }
    }
}
