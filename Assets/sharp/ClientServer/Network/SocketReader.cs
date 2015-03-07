using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Xml.Serialization;

using Tools;

namespace Network
{
    class SocketReader
    {   
        Socket socketRead;
        Action<MemoryStream> messageProcessor;
        Action<IOException> errorResponse;
        Action onSoftDisconnect;

        public SocketReader(Action<MemoryStream> messageProcessor_, Action<IOException> errorResponse_,
            Action onSoftDisconnect_, Socket socketRead_)
        {
            try
            {
                onSoftDisconnect = onSoftDisconnect_;
                messageProcessor = messageProcessor_;
                socketRead = socketRead_;
                errorResponse = errorResponse_;

                ThreadManager.NewThread(() => this.ProcessThread(),
                    () => TerminateThread(), "SocketReader " + NetTools.GetRemoteIP(socketRead).ToString());
                //new Thread(() => this.ProcessThread()).Start();
            }
            catch (Exception)
            {
                socketRead_.Close();
                throw;
            }
        }

        public void TerminateThread()
        {
            socketRead.Close();
        }

        void ProcessThread()
        {
            try
            {
                using (Stream readStream = new NetworkStream(socketRead, true))
                {
                    while (true)
                    {
                        int bt = readStream.ReadByte();

                        if (bt == -1)
                            throw new IOException("End of stream");
                        else if ((NetworkMessageType)bt == NetworkMessageType.SOFT_DISCONNECT)
                        {
                            readStream.WriteByte((byte)NetworkMessageType.SOFT_DISCONNECT);
                            
                            onSoftDisconnect.Invoke();
                            
                            return;
                        }
                        else if ((NetworkMessageType)bt == NetworkMessageType.MESSAGE)
                        {

                            //Console.WriteLine("Message received: {0}", (MessageType)bt);

                            messageProcessor(Serializer.DeserializeChunk(readStream));
                        }
                        else
                            throw new Exception(Log.StDump("Unexpected", bt, (NetworkMessageType)bt));
                    }
                }
            }
            catch (IOException ioe)
            {
                errorResponse(ioe);
                //Log.LogWriteLine("SocketReader terminated");
            }
        }
    }
}
