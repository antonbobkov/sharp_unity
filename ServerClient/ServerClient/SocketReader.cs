﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;

namespace ServerClient
{
    class SocketReader
    {   
        Socket socketRead;
        Action<Stream, MessageType> messageProcessor;

        public SocketReader(Action<Stream, MessageType> messageProcessor_, Socket socketRead_)
        {
            messageProcessor = messageProcessor_;
            socketRead = socketRead_;

            new Thread(() => this.ProcessThread()).Start();
        }

        void ProcessThread()
        {
            using (Stream readStream = new NetworkStream(socketRead, true))
            {
                while (true)
                {
                    int bt = readStream.ReadByte();

                    if (bt == -1)
                        messageProcessor(readStream, MessageType.DISCONNECT);

                    messageProcessor(readStream, (MessageType)bt);
                }
            }
        }
        
        public static object ReadSerializedMessage(Stream stm)
        {
            IFormatter formatter = new BinaryFormatter();
            return formatter.Deserialize(stm);
        }
    }
}
