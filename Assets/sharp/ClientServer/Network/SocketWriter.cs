using System.Net;
using System.Net.Sockets;
using System.Threading;
using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Xml.Serialization;
using System.Collections.Generic;

using Tools;

namespace Network
{
    enum SocketWriterMessageType { MESSAGE, TERMINATE, SOFT_DISCONNECT }

    class SocketWriterMessage
    {
        public SocketWriterMessageType type = SocketWriterMessageType.MESSAGE;
        public MemoryStream message = null;

        public SocketWriterMessage(SocketWriterMessageType swmt) { this.type = swmt; }
        public SocketWriterMessage(MemoryStream message) { this.message = message; }
    }


    
    class SocketWriter
    {
        public SocketWriter(IPEndPoint address, ActionSyncronizerProxy sync,
            Action<Exception, DisconnectType> errorResponse, Action onSoftDisconnect,
            Handshake info)
        {
            this.address = address;
            this.info = info;
           
            this.errorResponse = sync.Convert(errorResponse);
            this.onSoftDisconnect = sync.Convert(onSoftDisconnect);

            ThreadManager.NewThread(WritingThread, TerminateThread,
                "SocketWriter " + address);
        }

        public void SendMessage(SocketWriterMessage swm)
        {
            bcMessages.Add(swm);
        }
        public void TerminateThread()
        {
            bcMessages.Add(new SocketWriterMessage(SocketWriterMessageType.TERMINATE));
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
                        connectionStream.WriteByte((byte)NetworkMessageType.HANDSHAKE);

                        Serializer.SendMemoryStream(connectionStream, Serializer.SerializeGet(info));                        

                        if (info.hasExtraInfo)
                            Serializer.SendMemoryStream(connectionStream, info.ExtraInfo);
                    }

                }
                catch (Exception e)
                {
                    errorResponse.Invoke(e, DisconnectType.WRITE_CONNECT_FAIL);
                    return;
                }

                try
                {
                    using (NetworkStream connectionStream = new NetworkStream(writeSocket, false))
                        while (true)
                        {
                            SocketWriterMessage swm = bcMessages.Take();
                            if (swm.type == SocketWriterMessageType.TERMINATE)
                            {
                                //Log.LogWriteLine("SocketWriter terminated gracefully");
                                return;
                            }
                            else if (swm.type == SocketWriterMessageType.MESSAGE)
                            {
                                connectionStream.WriteByte((byte)NetworkMessageType.MESSAGE);

                                Serializer.SendMemoryStream(connectionStream, swm.message);
                            }
                            else if (swm.type == SocketWriterMessageType.SOFT_DISCONNECT)
                            {
                                if (!bcMessages.IsEmpty)
                                    continue;

                                connectionStream.WriteByte((byte)NetworkMessageType.SOFT_DISCONNECT);
                                int bt = connectionStream.ReadByte();
                                MyAssert.Assert(bt == (byte)NetworkMessageType.SOFT_DISCONNECT);
                                writeSocket.Close();

                                onSoftDisconnect.Invoke();

                                return;
                            }
                            else
                            {
                                throw new Exception(Log.StDump("Unexpected", swm.type));
                            }
                        }
                }
                catch (IOException ioe)
                {
                    errorResponse.Invoke(ioe, DisconnectType.WRITE);
                }

            }
        }

        public Queue<SocketWriterMessage> ExtractAllMessages() { return bcMessages.TakeAll(); }

        private IPEndPoint address;
        private BlockingCollection<SocketWriterMessage> bcMessages = new BlockingCollection<SocketWriterMessage>();
        
        private SyncAction<Exception, DisconnectType> errorResponse;
        private SyncAction onSoftDisconnect;
        
        private Handshake info;
    }
}
