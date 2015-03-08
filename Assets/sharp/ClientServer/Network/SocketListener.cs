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
        SyncAction<Handshake, Socket> processConnection;
        //Action<Exception> onHandshakeError;

        public SocketListener(ActionSyncronizerProxy sync, Action<Handshake, Socket> processConnection, Socket sckListen)
        {
            using (var h = DisposeHandle.Get(sckListen))
            {
                this.sckListen = sckListen;
                this.processConnection = sync.Convert(processConnection);
                //this.onHandshakeError = onHandshakeError;

                ThreadManager.NewThread(() => ProcessThread(),
                    () => TerminateThread(), "SocketListener " + NetTools.GetLocalIP(sckListen).ToString());

                h.Disengage();
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
            using (var h = DisposeHandle.Get(sckListen))
            {
                ThreadManager.NewThread(() => HandshakeThread(sckRead),
                    () => sckRead.Close(), "Incoming connection " + NetTools.GetRemoteIP(sckRead).ToString());
                h.Disengage();
            }
        }

        private void HandshakeThread(Socket sckRead)
        {
            try
            {
                using (var h = DisposeHandle.Get(sckRead))
                using (NetworkStream connectionStream = new NetworkStream(sckRead, false))
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

                    processConnection.Invoke(info, sckRead);
                    h.Disengage();
                }
            }
            catch (Exception e)
            {
                Log.Console("Error while processing handshake: {0}", e.Message);
                //onHandshakeError(e);
                throw;
            }
        }
    }

}
