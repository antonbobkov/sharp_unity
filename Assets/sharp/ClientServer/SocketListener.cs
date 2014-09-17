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
                ThreadManager.NewThread(() => ProcessThread(),
                    () => TerminateThread(), "SocketListener " + NetTools.GetLocalIP(sckListen).ToString());
                //new Thread(() => this.ProcessThread()).Start();
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
        public static Action<Socket> ProcessWithHandshake(Action<OverlayEndpoint, Socket> processConnection)
        {
            return (sck) => ConnectionHandshakeAsync(sck, processConnection);
        }

        public static void ConnectionHandshakeAsync(Socket sckRead, Action<OverlayEndpoint, Socket> processConnection)
        {
            try
            {
                ThreadManager.NewThread(() => ConnectionHandshake(sckRead, processConnection),
                    () => sckRead.Close(), "Incoming connection " + NetTools.GetRemoteIP(sckRead).ToString());
                //new Thread(() => ConnectionHandshake(sckRead, processConnection)).Start();
            }
            catch (Exception)
            {
                sckRead.Close();
                throw;
            }
        
        }
        
        public static void ConnectionHandshake(Socket sckRead, Action<OverlayEndpoint, Socket> processConnection)
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

                    OverlayEndpoint their_info = Serializer.Deserialize<OverlayEndpoint>(connectionStream);

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
