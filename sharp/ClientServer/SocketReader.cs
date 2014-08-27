using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Xml.Serialization;

namespace ServerClient
{
    class SocketReader
    {   
        Socket socketRead;
        Action<Stream, MessageType> messageProcessor;
        Action<IOException> errorResponse;

        public SocketReader(Action<Stream, MessageType> messageProcessor_, Action<IOException> errorResponse_, Socket socketRead_)
        {
            messageProcessor = messageProcessor_;
            socketRead = socketRead_;
            errorResponse = errorResponse_;

            new Thread(() => this.ProcessThread()).Start();
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

                        messageProcessor(readStream, (MessageType)bt);
                    }
                }
            }
            catch (IOException ioe)
            {
                errorResponse(ioe);
            }
        }
    }
}
