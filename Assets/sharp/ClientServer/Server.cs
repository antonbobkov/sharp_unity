using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;

using Tools;
using Network;


namespace ServerClient
{
    class DelayedAction
    {
        public OverlayEndpoint ep;
        public Action a;
    }

    class Server
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("server");

        Random r = new Random();

        //GameInfo gameInfo;
        HashSet<Point> worldsInProgress = new HashSet<Point>();

        List<IPEndPoint> validatorPool = new List<IPEndPoint>();
        
        List<Point> spawnWorlds = new List<Point>();
        Dictionary<Point, WorldInfo> worlds = new Dictionary<Point, WorldInfo>();

        OverlayHost myHost;

        int playerCounter = 1;
        static string PlayerNameMap(int value)
        {
            char[] baseChars = new char[] { '0','1','2','3','4','5','6','7','8','9',
            'A','B','C','D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z',
            'a','b','c','d','e','f','g','h','i','j','k','l','m','n','o','p','q','r','s','t','u','v','w','x','y','z'};

            MyAssert.Assert(value >= 0);

            string result = string.Empty;
            int targetBase = baseChars.Length;

            do
            {
                result = baseChars[value % targetBase] + result;
                value = value / targetBase;
            }
            while (value > 0);

            return result;
        }
        Dictionary<Guid, DelayedAction> delayedActions = new Dictionary<Guid, DelayedAction>();

        public OverlayEndpoint Address { get { return myHost.Address; } }

        public Server(GlobalHost globalHost, ActionSyncronizer sync)
        {
            myHost = globalHost.NewHost(Server.hostName, Game.Convert(AssignProcessor),
                BasicInfo.GenerateHandshake(NodeRole.SERVER));
            myHost.onNewConnectionHook = ProcessNewConnection;
            myHost.AddInactivityTimeout(sync, TimeSpan.FromSeconds(5));

            //Action<ForwardFunctionCall> onChange = (ffc) => myHost.BroadcastGroup(Client.hostName, MessageType.GAME_INFO_VAR_CHANGE, ffc.Serialize());
            //gameInfo = new ForwardProxy<GameInfo>(new GameInfo(), onChange).GetProxy();
        }

        Game.MessageProcessor AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (role == NodeRole.CLIENT)
                return ProcessClientMessage;

            if (role == NodeRole.WORLD_VALIDATOR)
                return ProcessWorldMessage;

            if (role == NodeRole.PLAYER_AGENT)
            {
                PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);
                return (mt, stm, nd) => ProcessPlayerMessage(mt, stm, nd, inf);
            }

            throw new Exception(Log.StDump(n.info, role, "unexpected"));
        }

        void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.NEW_PLAYER_REQUEST)
            {
                Guid player = Serializer.Deserialize<Guid>(stm);
                OnNewPlayerRequest(player, n.info.remote);
            }
            else if (mt == MessageType.NEW_WORLD_REQUEST)
            {
                Point worldPos = Serializer.Deserialize<Point>(stm);
                OnNewWorldRequest(worldPos);
            }
            else if (mt == MessageType.NEW_VALIDATOR)
            {
                OnNewValidator(n.info.remote.addr);
            }
            else if (mt == MessageType.ACCEPT)
            {
                Guid actionId = Serializer.Deserialize<Guid>(stm);
                OnAccept(actionId, n.info.remote);
            }
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessWorldMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.NEW_WORLD_REQUEST)
            {
                Point worldPos = Serializer.Deserialize<Point>(stm);
                OnNewWorldRequest(worldPos);
                //n.SoftDisconnect();
            }
            else
                throw new Exception("Client.ProcessWorldMessage bad message type " + mt.ToString());
        }
        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
            {
                OnSpawnRequest(inf);
                //n.SoftDisconnect();
            }
            else
                throw new Exception(Log.StDump("unexpected", mt));
        }

        void ProcessNewConnection(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;

            if (remoteName == Client.hostName)
                OnNewClient(n);
        }
        void OnNewClient(Node n)
        {
            //n.SendMessage(MessageType.GAME_INFO_VAR_INIT, gameInfo.Serialize());
        }

        void OnNewPlayerRequest(Guid playerId, OverlayEndpoint playerClient)
        {
            // ! Worry about non-atomicity. Two similar requests at once? See OnNewWorldRequest
            
            Guid actionId = Guid.NewGuid();
            string name = PlayerNameMap(playerCounter++);
            
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName("validator player " + name));

            OverlayEndpoint playerNewHost = new OverlayEndpoint(playerClient.addr, new OverlayHostName("agent player " + name));
            PlayerInfo playerInfo = new PlayerInfo(playerId, playerNewHost, validatorHost, name);

            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);
            myHost.SendMessage(validatorClient, MessageType.PLAYER_VALIDATOR_ASSIGN, actionId, playerInfo);

            DelayedAction da = new DelayedAction()
            {
                ep = validatorClient,
                a = () =>
                {
                    //gameInfo.NET_AddPlayer(playerInfo);
                    myHost.ConnectSendMessage(playerClient, MessageType.NEW_PLAYER_REQUEST_SUCCESS, playerInfo);
                    Log.Console("New player " + name + " validated by " + validatorHost.addr);
                }
            };

            delayedActions.Add(actionId, da);
        }
        void OnNewValidator(IPEndPoint ip)
        {
            MyAssert.Assert(!validatorPool.Where((valip) => valip == ip).Any());
            validatorPool.Add(ip);
        }

        MyColor RandomColor(Point p)
        {
            float fScale = .1f;
            Point pShift = new Point(10, 10);

            Func<Point, byte> gen = (pos) => Convert.ToByte(Math.Round((Noise.Generate((float)pos.x * fScale, (float)pos.y * fScale) + 1f) / 2 * 255));

            //p += pShift;
            Byte R = gen(p);

            p += pShift;
            Byte G = gen(p);

            p += pShift;
            Byte B = gen(p);

            return new MyColor(R, G, B);
        }

        void OnNewWorldRequest(Point worldPos)
        {
            //if (gameInfo.TryGetWorldByPos(worldPos) != null)
            //    return;
            if (worlds.ContainsKey(worldPos))
                return;
            if (worldsInProgress.Contains(worldPos))
                return;
            worldsInProgress.Add(worldPos);
            
            Guid validatorId = Guid.NewGuid();
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName("host world " + worldPos));

            WorldInfo info = new WorldInfo(worldPos, validatorHost);
            WorldInitializer init = new WorldInitializer(r.Next(), RandomColor(worldPos));

            if (worldPos == Point.Zero)
            //if ((worldPos.x % 2 == 0) && (worldPos.y % 2 == 0))
                init.hasSpawn = true;

            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);
            myHost.SendMessage(validatorClient, MessageType.WORLD_VALIDATOR_ASSIGN, validatorId, info, init);

            DelayedAction da = new DelayedAction()
            {
                ep = validatorClient,
                a = () =>
                {
                    if (init.hasSpawn == true)
                        spawnWorlds.Add(worldPos);

                    worldsInProgress.Remove(worldPos);
                    worlds.Add(info.position, info);

                    //Log.LogWriteLine("New world " + worldPos + " validated by " + validatorHost.addr);

                    foreach(Point p in Point.SymmetricRange(Point.One))
                    {
                        if (p == Point.Zero)
                            continue;

                        Point neighborPos = p + info.position;

                        if(!worlds.ContainsKey(neighborPos))
                            continue;

                        WorldInfo neighborWorld = worlds[neighborPos];

                        myHost.ConnectSendMessage(neighborWorld.host, MessageType.NEW_NEIGHBOR, info);
                        myHost.ConnectSendMessage(info.host, MessageType.NEW_NEIGHBOR, neighborWorld);
                    }

                    //gameInfo.NET_AddWorld(info);
                    //myHost.BroadcastGroup(Client.hostName, MessageType.NEW_WORLD, info);
                }
            };

            delayedActions.Add(validatorId, da);

        }

        void OnSpawnRequest(PlayerInfo inf)
        {
            if (!spawnWorlds.Any())
            {
                Log.Dump("No spawn worlds", inf);
                return;
            }

            Point spawnWorldPos = spawnWorlds.Random(n => r.Next(n));

            WorldInfo spawnWorld = worlds.GetValue(spawnWorldPos);

            myHost.ConnectSendMessage(spawnWorld.host, MessageType.SPAWN_REQUEST, inf);
        }

        void OnAccept(Guid id, OverlayEndpoint remote)
        {
            DelayedAction da = delayedActions.GetValue(id);
            delayedActions.Remove(id);

            MyAssert.Assert(da.ep == remote);

            da.a();
        }

        public void PrintStats()
        {
            myHost.PrintStats();
        }
    }
}
