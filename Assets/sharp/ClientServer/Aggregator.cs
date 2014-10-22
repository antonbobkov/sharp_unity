using System;
using ServerClient.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;


namespace ServerClient
{
    class Aggregator
    {
        public ActionSyncronizer sync = new ActionSyncronizer();
        public GlobalHost host;

        public Client myClient;
        public Server myServer = null;

        public Dictionary<Point, WorldValidator> worldValidators = new Dictionary<Point, WorldValidator>();
        public Dictionary<Guid, PlayerValidator> playerValidators = new Dictionary<Guid, PlayerValidator>();
        public Dictionary<Guid, PlayerAgent> playerAgents = new Dictionary<Guid, PlayerAgent>();

        public Action<PlayerInfo, PlayerData> onNewPlayerDataHook = (a, b) => { };
        public Action<PlayerInfo, PlayerData> onPlayerNewRealm = (a, b) => { };
        
        public Aggregator()
        {
            lock (sync.syncLock)
            {
                host = new GlobalHost(sync.GetAsDelegate());
                myClient = new Client(host, this);
            }
        }

        public static IPEndPoint ParseParamForIP(List<string> param)
        {
            IPAddress ip = NetTools.GetMyIP();
            int port = GlobalHost.nStartPort;

            foreach (var s in param)
            {
                int parsePort;
                if (int.TryParse(s, out parsePort))
                {
                    port = parsePort;
                    continue;
                }

                IPAddress parseIp;
                if (IPAddress.TryParse(s, out parseIp))
                {
                    ip = parseIp;
                    continue;
                }
            }

            return new IPEndPoint(ip, port);
        }

        public void ParamConnect(List<string> param, bool mesh = false)
        {
            IPEndPoint ep = ParseParamForIP(param);
            Log.LogWriteLine("Connecting to {0} {1}", ep.Address, ep.Port);
            if (!myClient.TryConnect(ep))
                Log.LogWriteLine("Already connected/connecting");
        }
        public void StartServer()
        {
            MyAssert.Assert(myServer == null);
            myServer = new Server(host);
            myClient.OnServerAddress(myServer.Address);
        }

        public void AddWorldValidator(WorldInfo info, WorldInitializer init)
        {
            MyAssert.Assert(!worldValidators.ContainsKey(info.worldPos));
            worldValidators.Add(info.worldPos, new WorldValidator(info, init, host, myClient.gameInfo, myClient.serverHost));
        }
        public void AddPlayerValidator(PlayerInfo info)
        {
            MyAssert.Assert(!playerValidators.ContainsKey(info.id));
            playerValidators.Add(info.id, new PlayerValidator(info, host, myClient.gameInfo));
        }
        public void AddPlayerAgent(PlayerInfo info)
        {
            MyAssert.Assert(!playerAgents.ContainsKey(info.id));

            PlayerAgent pa = new PlayerAgent(info, myClient.gameInfo, host, myClient.serverHost);

            pa.onNewPlayerDataHook = (pd) => onNewPlayerDataHook(pa.info, pd);
            pa.onPlayerNewRealm = (pd) => onPlayerNewRealm(pa.info, pd);
            
            playerAgents.Add(info.id, pa);
        }

        public void SpawnAll()
        {
            foreach (Guid id in myClient.myPlayerAgents)
                playerAgents.GetValue(id).Spawn();
        }
    }
}
