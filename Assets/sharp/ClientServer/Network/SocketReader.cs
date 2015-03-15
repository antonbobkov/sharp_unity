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
        
        SyncAction<MemoryStream> messageProcessor;
        SyncAction<IOException> errorResponse;
        SyncAction onSoftDisconnect;

        public SocketReader(ActionSyncronizerProxy sync,
            Action<MemoryStream> messageProcessor, Action<IOException> errorResponse, Action onSoftDisconnect,
            Socket socketRead)
        {
            using (var h = DisposeHandle.Get(socketRead))
            {
                this.onSoftDisconnect = sync.Convert(onSoftDisconnect);
                this.messageProcessor = sync.Convert(messageProcessor);
                this.errorResponse = sync.Convert(errorResponse);

                this.socketRead = socketRead;
                
                ThreadManager.NewThread(() => this.ProcessThread(),
                    () => TerminateThread(), "SocketReader " + NetTools.GetRemoteIP(socketRead).ToString());
                h.Disengage();
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
                            onSoftDisconnect.Invoke();

                            readStream.WriteByte((byte)NetworkMessageType.SOFT_DISCONNECT);
                            
                            return;
                        }
                        else if ((NetworkMessageType)bt == NetworkMessageType.MESSAGE)
                        {

                            //Console.WriteLine("Message received: {0}", (MessageType)bt);

                            messageProcessor.Invoke(Serializer.DeserializeChunk(readStream));
                        }
                        else
                            throw new Exception(Log.StDump("Unexpected", bt, (NetworkMessageType)bt));
                    }
                }
            }
            catch (IOException ioe)
            {
                errorResponse.Invoke(ioe);
                //Log.LogWriteLine("SocketReader terminated");
            }
        }
    }
}
