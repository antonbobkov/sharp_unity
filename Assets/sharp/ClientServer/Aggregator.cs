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
using System.Xml.Serialization;



namespace ServerClient
{
    public class GameConfig
    {
        public bool startServer = false;
        public bool validate = true;
        public int aiPlayers = 0;
        public List<string> meshIP = new List<string>() { "default" };

        public static GameConfig ReadConfig(string filename)
        {
            XmlSerializer ser = new XmlSerializer(typeof(GameConfig));
            GameConfig cfg = new GameConfig();

            try
            {
                StreamReader sw = new StreamReader(filename);
                cfg = (GameConfig)ser.Deserialize(sw);
                sw.Close();
            }
            catch (FileNotFoundException)
            {
                ILog.Console("Cannot find config file " + filename + ", default one will be created");
                StreamWriter sw = new StreamWriter(filename);
                ser.Serialize(sw, cfg);
            }
            

            return cfg;
        }
    }

    class Aggregator
    {
        public ActionSyncronizer sync = new ActionSyncronizer();
        public GlobalHost host;

        public Client myClient;
        public Server myServer = null;

        public Dictionary<Point, WorldValidator> worldValidators = new Dictionary<Point, WorldValidator>();
        public Dictionary<Guid, PlayerValidator> playerValidators = new Dictionary<Guid, PlayerValidator>();
        public Dictionary<Guid, PlayerAgent> playerAgents = new Dictionary<Guid, PlayerAgent>();

        public Action<PlayerAgent> onNewPlayerAgentHook = (a) => { };
        
        public Aggregator()
        {
            host = new GlobalHost(sync.GetAsDelegate());
            myClient = new Client(host, this);
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
            MyAssert.Assert(!worldValidators.ContainsKey(info.position));
            worldValidators.Add(info.position, new WorldValidator(info, init, host, myClient.serverHost));
        }
        public void AddPlayerValidator(PlayerInfo info)
        {
            MyAssert.Assert(!playerValidators.ContainsKey(info.id));
            playerValidators.Add(info.id, new PlayerValidator(info, host));
        }
        public void AddPlayerAgent(PlayerInfo info)
        {
            MyAssert.Assert(!playerAgents.ContainsKey(info.id));

            PlayerAgent pa = new PlayerAgent(info, host, myClient.serverHost, myClient);

            onNewPlayerAgentHook(pa);
            
            playerAgents.Add(info.id, pa);
        }

        public void SpawnAll()
        {
            foreach (Guid id in myClient.myPlayerAgents)
                playerAgents.GetValue(id).Spawn();
        }
    }
}
