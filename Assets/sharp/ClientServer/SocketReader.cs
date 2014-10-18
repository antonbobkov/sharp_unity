﻿using System;
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
            try
            {
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

                        //Console.WriteLine("Message received: {0}", (MessageType)bt);
                       
                        messageProcessor(Serializer.DeserializeChunk(readStream), (MessageType)bt);
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
