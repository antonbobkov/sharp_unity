using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Tools;

namespace Network
{
    class SocketListener
    {
        Socket sckListen;
        Action<Handshake, Socket> processConnection;
        //Action<Exception> onHandshakeError;

        public SocketListener(Action<Handshake, Socket> processConnection, Socket sckListen)
        {
            try
            {
                this.sckListen = sckListen;
                this.processConnection = processConnection;
                //this.onHandshakeError = onHandshakeError;

                ThreadManager.NewThread(() => ProcessThread(),
                    () => TerminateThread(), "SocketListener " + NetTools.GetLocalIP(sckListen).ToString());
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
                        ProcessHandshakeAsync(sck);
                    }
            }
            catch (SocketException)
            {
                //Log.LogWriteLine("SocketListener terminated");
            }
        }

        private void ProcessHandshakeAsync(Socket sckRead)
        {
            try
            {
                ThreadManager.NewThread(() => HandshakeThread(sckRead),
                    () => sckRead.Close(), "Incoming connection " + NetTools.GetRemoteIP(sckRead).ToString());
            }
            catch (Exception)
            {
                sckRead.Close();
                throw;
            }

        }

        private void HandshakeThread(Socket sckRead)
        {
            using (NetworkStream connectionStream = new NetworkStream(sckRead, false))
            {
                try
                {
                    int nTp = connectionStream.ReadByte();

                    if (nTp != (int)NetworkMessageType.HANDSHAKE)
                    {
                        //Log.LogWriteLine("Invalid incoming connection message (expecting handshake): type {0} {1}", nTp, (MessageType)nTp);

                        sckRead.Close();
                        throw new Exception("Invalid incoming connection message (expecting handshake): type " + nTp + " " + (NetworkMessageType)nTp);
                    }

                    Handshake info = Serializer.Deserialize<Handshake>(Serializer.DeserializeChunk(connectionStream));

                    // swap
                    OverlayEndpoint remote = info.local;
                    OverlayEndpoint local = info.remote;

                    info.remote = remote;
                    info.local = local;

                    // read extra information
                    if (info.hasExtraInfo)
                        info.ExtraInfo = Serializer.DeserializeChunk(connectionStream);

                    processConnection(info, sckRead);
                }
                catch (Exception e)
                {
                    Log.Console("Error while processing handshake: {0}", e.Message);
                    //onHandshakeError(e);
                    sckRead.Close();
                    throw;
                }
            }
        }
    }

}
