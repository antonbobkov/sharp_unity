using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Xml.Serialization;

using Tools;
using Network;



namespace ServerClient
{
    public class GameInstanceConifg
    {
        public bool validate = true;
        public int aiPlayers = 0;
    }
    
    public class GameConfig
    {
        public bool startServer = false;

        public GameInstanceConifg serverConfig = new GameInstanceConifg();
        public GameInstanceConifg clientConfig = new GameInstanceConifg();

        public List<string> meshIPs = new List<string>() { "default" };
        public string myIP = "default";

        public static GameConfig ReadConfig(string filename)
        {
            XmlSerializer ser = new XmlSerializer(typeof(GameConfig));
            GameConfig cfg = new GameConfig();

            try
            {
                using(StreamReader sw = new StreamReader(filename))
                    cfg = (GameConfig)ser.Deserialize(sw);
            }
            catch (FileNotFoundException)
            {
                Log.Console("Cannot find config file " + filename + ", default one will be created");
                
                using(StreamWriter sw = new StreamWriter(filename))
                    ser.Serialize(sw, cfg);
            }

            
            return cfg;
        }
        public static IPAddress GetIP(GameConfig cfg)
        {
            if (cfg.myIP == "default")
                return NetTools.GetMyIP();
            else
                return IPAddress.Parse(cfg.myIP);
        }
    }

    class Aggregator
    {
        public ActionSyncronizer sync;
        public GlobalHost host;

        public Client myClient;
        public Server myServer = null;

        public Dictionary<Point, WorldValidator> worldValidators = new Dictionary<Point, WorldValidator>();
        public Dictionary<Guid, PlayerValidator> playerValidators = new Dictionary<Guid, PlayerValidator>();
        public Dictionary<Guid, PlayerAgent> playerAgents = new Dictionary<Guid, PlayerAgent>();

        public Action<PlayerAgent> onNewPlayerAgentHook = (a) => { };

        public Aggregator(IPAddress myIP)
        {
            sync = new ActionSyncronizer();
            host = new GlobalHost(sync.GetProxy(), myIP);
            myClient = new Client(host, this);
        }

        public static IPEndPoint ParseParamForIP(List<string> param, IPAddress defaultIP)
        {
            IPAddress ip = defaultIP;
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

        public void ParamConnect(List<string> param, IPAddress defaultIP)
        {
            IPEndPoint ep = ParseParamForIP(param, defaultIP);
            Log.Console("Connecting to {0} {1}", ep.Address, ep.Port);
            if (!myClient.TryConnect(ep))
                Log.Console("Already connected/connecting");
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
