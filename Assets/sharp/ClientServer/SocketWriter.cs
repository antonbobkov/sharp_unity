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
                ThreadManager.NewThread(() => ProcessThread(),
                    () => TerminateThread(), "SocketWriter " + NetTools.GetRemoteIP(socketWrite).ToString());
                //new Thread(() => this.ProcessThread()).Start();
            }
            catch (Exception)
            {
                socketWrite_.Close();
                throw;
            }
        }

        public static MemoryStream SerializeMessage(MessageType mt, params object[] messages)
        {
            MemoryStream ms = new MemoryStream();

            //Console.WriteLine("Message sent: {0}", mt);

            ms.WriteByte((byte)mt);

            Serializer.Serialize(ms, messages);
            Serializer.lastWrite = new ChunkDebug(ms, true);

            ms.Position = 0;

            return ms;
        }
        
        public void SendMessage(MessageType mt, params object[] messages)
        {
            MemoryStream ms = SerializeMessage(mt, messages);
            
            bcMessages.Add(stm => Serializer.SendStream(stm, ms));
        }

        public void SendStream(MemoryStream stream)
        {
            MemoryStream ms = new MemoryStream(stream.ToArray()); // copy for sync

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
