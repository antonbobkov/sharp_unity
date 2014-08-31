﻿using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ServerClient
{
    class SocketListener
    {
        Action<Socket> processConnection;
        Socket sckListen;

        public SocketListener(Action<Socket> processConnection_, Socket sckListen_)
        {
            try
            {
                processConnection = processConnection_;
                sckListen = sckListen_;
                new Thread(() => this.ProcessThread()).Start();
            }
            catch (Exception)
            {
                sckListen.Close();
                throw;
            }
        }

        public void TerminateThread()
        {
            sckListen.Close();
        }

        void ProcessThread()
        {
            try
            {
                using (sckListen)
                    while (true)
                    {
                        Socket sck = sckListen.Accept();
                        processConnection(sck);
                    }
            }
            catch (SocketException)
            {
                //Log.LogWriteLine("SocketListener terminated");
            }
        }
    }

    class ConnectionProcessor
    {
        public static Action<Socket> ProcessWithHandshake(Action<Handshake, Socket> processConnection)
        {
            return (sck) => ConnectionHandshakeAsync(sck, processConnection);
        }

        public static void ConnectionHandshakeAsync(Socket sckRead, Action<Handshake, Socket> processConnection)
        {
            try
            {
                new Thread(() => ConnectionHandshake(sckRead, processConnection)).Start();
            }
            catch (Exception)
            {
                sckRead.Close();
                throw;
            }
        
        }
        
        public static void ConnectionHandshake(Socket sckRead, Action<Handshake, Socket> processConnection)
        {
            using (NetworkStream connectionStream = new NetworkStream(sckRead, false))
            {
                try
                {
                    int nTp = connectionStream.ReadByte();

                    if (nTp != (int)MessageType.HANDSHAKE)
                    {
                        Log.LogWriteLine("Invalid incoming connection message (expecting handshake): type {0} {1}", nTp, (MessageType)nTp);

                        sckRead.Close();
                        return;
                    }

                    Handshake their_info = Serializer.Deserialize<Handshake>(connectionStream);

                    processConnection(their_info, sckRead);
                }
                catch (Exception e)
                {
                    Log.LogWriteLine("Error while processing handshake: {0}", e.Message);

                    sckRead.Close();
                    return;
                }
            }
        }
    }
}