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
    [Serializable]
    public class GameInfoSerialized
    {
        public PlayerInfo[] players;
        public WorldInfo[] worlds;

        public GameInfoSerialized() { }
    }

    class GameInfo : MarshalByRefObject
    {
        // ----- constructors -----
        public GameInfo() { }

        public GameInfoSerialized Serialize()
        {
            return new GameInfoSerialized() { players = playerById.Values.ToArray(), worlds = worldByPoint.Values.ToArray() };
        }
        public void Deserialize(GameInfoSerialized info)
        {
            foreach (PlayerInfo p in info.players)
                NET_AddPlayer(p);
            foreach (WorldInfo w in info.worlds)
                NET_AddWorld(w);
        }

        // ----- read only infromation -----
        public NodeRole GetRoleOfHost(OverlayEndpoint host) { return roles.GetValue(host); }
        public NodeRole? TryGetRoleOfHost(OverlayEndpoint host)
        {
            if (roles.ContainsKey(host))
                return roles[host];
            else
                return null;
        }

        public PlayerInfo GetPlayerByHost(OverlayEndpoint host) { return playerByHost.GetValue(host); }
        public WorldInfo GetWorldByHost(OverlayEndpoint host) { return worldByHost.GetValue(host); }

        public PlayerInfo GetPlayerById(Guid player) { return playerById.GetValue(player); }
        public WorldInfo GetWorldByPos(Point pos) { return worldByPoint.GetValue(pos); }

        public WorldInfo? TryGetWorldByPos(Point pos)
        {
            if (worldByPoint.ContainsKey(pos))
                return worldByPoint[pos];
            else
                return null;
        }

        public OverlayEndpoint GetPlayerHost(Guid player) { return playerById.GetValue(player).playerHost; }
        public OverlayEndpoint GetPlayerValidatorHost(Guid player) { return playerById.GetValue(player).validatorHost; }
        public OverlayEndpoint GetWorldHost(Point worldPos) { return worldByPoint.GetValue(worldPos).host; }

        // ----- modifiers -----
        [Forward]
        public void NET_AddPlayer(PlayerInfo info)
        {
            roles.Add(info.playerHost, NodeRole.PLAYER_AGENT);
            roles.Add(info.validatorHost, NodeRole.PLAYER_VALIDATOR);

            playerById.Add(info.id, info);
            playerByHost.Add(info.playerHost, info);
            playerByHost.Add(info.validatorHost, info);

            onNewPlayer.Invoke(info);
        }
        [Forward]
        public void NET_AddWorld(WorldInfo info)
        {
            roles.Add(info.host, NodeRole.WORLD_VALIDATOR);

            worldByPoint.Add(info.worldPos, info);
            worldByHost.Add(info.host, info);

            onNewWorld.Invoke(info);
        }

        // ----- hooks -----
        public Action<PlayerInfo> onNewPlayer = (inf) => { };
        public Action<WorldInfo> onNewWorld = (inf) => { };

        // ----- private data -----
        Dictionary<OverlayEndpoint, NodeRole> roles = new Dictionary<OverlayEndpoint, NodeRole>();
        Dictionary<Guid, PlayerInfo> playerById = new Dictionary<Guid, PlayerInfo>();
        Dictionary<OverlayEndpoint, PlayerInfo> playerByHost = new Dictionary<OverlayEndpoint, PlayerInfo>();
        Dictionary<Point, WorldInfo> worldByPoint = new Dictionary<Point, WorldInfo>();
        Dictionary<OverlayEndpoint, WorldInfo> worldByHost = new Dictionary<OverlayEndpoint, WorldInfo>();
    }

    class DelayedAction
    {
        public OverlayEndpoint ep;
        public Action a;
    }

    class Server
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("server");

        Random r = new Random();

        GameInfo gameInfo;
        HashSet<Point> worldsInProgress = new HashSet<Point>();

        List<IPEndPoint> validatorPool = new List<IPEndPoint>();
        List<Point> spawnWorlds = new List<Point>();

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

        public Server(GlobalHost globalHost)
        {
            myHost = globalHost.NewHost(Server.hostName, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;

            Action<ForwardFunctionCall> onChange = (ffc) => myHost.BroadcastGroup(Client.hostName, MessageType.GAME_INFO_VAR_CHANGE, ffc.Serialize());
            gameInfo = new ForwardProxy<GameInfo>(new GameInfo(), onChange).GetProxy();
        }

        Node.MessageProcessor AssignProcessor(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)
                return ProcessClientMessage;

            NodeRole? role = gameInfo.TryGetRoleOfHost(n.info.remote);

            if (role == NodeRole.WORLD_VALIDATOR)
                return ProcessWorldMessage;

            if (role == NodeRole.PLAYER_AGENT)
            {
                //PlayerInfo inf = gameInfo.GetPlayerByHost(n.info.remote);
                //return (mt, stm, nd) => ProcessPlayerMessage(mt, stm, nd, inf);

                return ProcessGenericMessage;
            }

            throw new InvalidOperationException("Server.AssignProcessor unexpected connection " + n.info.remote + " " + role);
        }

        Dictionary<OverlayEndpoint, Node.MessageProcessor> messageRelay = new Dictionary<OverlayEndpoint, Node.MessageProcessor>();
        void ProcessGenericMessage(MessageType mt, Stream stm, Node n)
        {
            OverlayEndpoint remote = n.info.remote;

            if (messageRelay.ContainsKey(remote))
            {
                messageRelay[remote].Invoke(mt, stm, n);
                return;
            }

            if (mt != MessageType.ROLE)
                throw new Exception(Log.StDump(mt, remote, "unexpected"));

            NodeRole nr = Serializer.Deserialize<NodeRole>(stm);

            if (nr == NodeRole.PLAYER_AGENT)
            {
                PlayerInfo playerInfo = Serializer.Deserialize<PlayerInfo>(stm);
                messageRelay.Add(remote, (message, stream, node) => ProcessPlayerMessage(message, stream, node, playerInfo));
            }
            else
                throw new Exception(Log.StDump(nr, remote, "unexpected"));
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
            }
            else
                throw new Exception("Client.ProcessWorldMessage bad message type " + mt.ToString());
        }
        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
                OnSpawnRequest(inf);
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
            n.SendMessage(MessageType.GAME_INFO_VAR_INIT, gameInfo.Serialize());
        }

        void OnNewPlayerRequest(Guid playerId, OverlayEndpoint playerHost)
        {
            // ! Worry about non-atomicity. Two similar requests at once? See OnNewWorldRequest
            
            Guid actionId = Guid.NewGuid();
            string name = PlayerNameMap(playerCounter++);
            
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName("validator player " + name));

            OverlayEndpoint playerNewHost = new OverlayEndpoint(playerHost.addr, new OverlayHostName("agent player " + name));
            PlayerInfo info = new PlayerInfo(playerId, playerNewHost, validatorHost, name);

            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);
            myHost.SendMessage(validatorClient, MessageType.PLAYER_VALIDATOR_ASSIGN, actionId, info);

            DelayedAction da = new DelayedAction()
            {
                ep = validatorClient,
                a = () =>
                {
                    gameInfo.NET_AddPlayer(info);
                    //myHost.BroadcastGroup(Client.hostName, MessageType.NEW_PLAYER, info);
                }
            };

            delayedActions.Add(actionId, da);
        }
        void OnNewValidator(IPEndPoint ip)
        {
            MyAssert.Assert(!validatorPool.Where((valip) => valip == ip).Any());
            validatorPool.Add(ip);
        }
        
        void OnNewWorldRequest(Point worldPos)
        {
            if (gameInfo.TryGetWorldByPos(worldPos) != null)
                return;
            if (worldsInProgress.Contains(worldPos))
                return;
            worldsInProgress.Add(worldPos);
            
            Guid validatorId = Guid.NewGuid();
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName("host world " + worldPos));

            WorldInfo info = new WorldInfo(worldPos, validatorHost);
            WorldInitializer init = new WorldInitializer(r.Next());

            //if (worldPos == new Point(0, 0))
            if ((worldPos.x % 2 == 0) && (worldPos.y % 2 == 0))
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

                    gameInfo.NET_AddWorld(info);
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

            WorldInfo spawnWorld = gameInfo.GetWorldByPos(spawnWorldPos);

            myHost.ConnectSendMessage(spawnWorld.host, MessageType.SPAWN_REQUEST, inf.id);
        }

        void OnAccept(Guid id, OverlayEndpoint remote)
        {
            DelayedAction da = delayedActions.GetValue(id);
            delayedActions.Remove(id);

            MyAssert.Assert(da.ep == remote);

            da.a();
        }
    }
}
