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
    class Server
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("server");

        Random r = new Random();

        List<IPEndPoint> validatorPool = new List<IPEndPoint>();
        
        List<Point> spawnWorlds = new List<Point>();
        Dictionary<Point, WorldInfo> worlds = new Dictionary<Point, WorldInfo>();
        Dictionary<Guid, PlayerInfo> players = new Dictionary<Guid, PlayerInfo>();

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

        RemoteActionRepository remoteActions = new RemoteActionRepository();
        HashSet<Guid> playerLocks = new HashSet<Guid>();
        HashSet<Point> worldLocks = new HashSet<Point>();

        public OverlayEndpoint Address { get { return myHost.Address; } }

        int serverSpawnDensity;

        public Server(GlobalHost globalHost, ActionSyncronizer sync, int serverSpawnDensity)
        {
            myHost = globalHost.NewHost(Server.hostName, Game.Convert(AssignProcessor),
                BasicInfo.GenerateHandshake(NodeRole.SERVER), Aggregator.shortInactivityWait);

            this.serverSpawnDensity = serverSpawnDensity;
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
                return (mt, stm, nd) => ProcessPlayerMessage(mt, stm, nd, inf.id);
            }

            if (role == NodeRole.PLAYER_VALIDATOR)
            {
                PlayerInfo inf = Serializer.Deserialize<PlayerInfo>(nodeInfo);
                return (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd, inf);
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
                OnNewWorldRequest(worldPos, null, 0);
            }
            else if (mt == MessageType.NEW_VALIDATOR)
            {
                OnNewValidator(n.info.remote.addr);
            }
            else if (mt == MessageType.RESPONSE)
                RemoteAction.Process(remoteActions, n, stm);
            else if (mt == MessageType.STOP_VALIDATING)
                OnStopValidating(n);
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessWorldMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.NEW_WORLD_REQUEST)
            {
                Point worldPos = Serializer.Deserialize<Point>(stm);
                OnNewWorldRequest(worldPos, null, 0);
            }
            else if (mt == MessageType.WORLD_HOST_DISCONNECT)
            {
                WorldInitializer w = Serializer.Deserialize<WorldInitializer>(stm);
                OnWorldHostDisconnect(w);
            }
            else
                throw new Exception(Log.StDump("bad message type", mt));
        }
        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, Guid id)
        {
            if (mt == MessageType.SPAWN_REQUEST)
            {
                OnSpawnRequest(id);
                //n.SoftDisconnect();
            }
            else
                throw new Exception(Log.StDump("unexpected", mt));
        }
        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.PLAYER_HOST_DISCONNECT)
            {
                PlayerData pd = Serializer.Deserialize<PlayerData>(stm);
                OnPlayerHostDisconnect(inf, pd);
            }
            else
                throw new Exception(Log.StDump("bad message type", mt));
        }

        void NewPlayerProcess(PlayerInfo playerInfo, PlayerData pd, ManualLock<Guid> lck)
        {
            OverlayEndpoint validatorClient = new OverlayEndpoint(playerInfo.validatorHost.addr, Client.hostName);
            OverlayEndpoint playerClient = new OverlayEndpoint(playerInfo.playerHost.addr, Client.hostName);

            RemoteAction
                .Send(myHost, validatorClient, MessageType.PLAYER_VALIDATOR_ASSIGN, playerInfo, pd)
                .Respond(remoteActions, lck, (res, stm) =>
                {
                    if (playerInfo.generation == 0)
                    {
                        MyAssert.Assert(!players.ContainsKey(playerInfo.id));
                        players.Add(playerInfo.id, playerInfo);
                    }
                    else
                    {
                        MyAssert.Assert(players.ContainsKey(playerInfo.id));
                        players[playerInfo.id] = playerInfo;
                    }

                    myHost.ConnectSendMessage(playerClient, MessageType.NEW_PLAYER_REQUEST_SUCCESS, playerInfo);

                    Log.Console("New player " + playerInfo.name + " validated by " + playerInfo.validatorHost.addr);
                });
        }

        void OnNewPlayerRequest(PlayerInfo inf, PlayerData pd)
        {
            MyAssert.Assert(players.ContainsKey(inf.id));
            MyAssert.Assert(!playerLocks.Contains(inf.id));

            if (!validatorPool.Any())
                throw new Exception("no validators!");

            ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, inf.id);

            inf.generation++;

            string hostName = "validator player " + inf.name + " (" + inf.generation + ")"; ;
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)),
                new OverlayHostName(hostName));

            inf.validatorHost = validatorHost;

            NewPlayerProcess(inf, pd, lck);
        }
        void OnNewPlayerRequest(Guid playerId, OverlayEndpoint playerClient)
        {
            MyAssert.Assert(!players.ContainsKey(playerId));
            MyAssert.Assert(!playerLocks.Contains(playerId));

            if (!validatorPool.Any())
                throw new Exception("no validators!");

            ManualLock<Guid> lck = new ManualLock<Guid>(playerLocks, playerId);

            string name = PlayerNameMap(playerCounter++);

            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)),
                new OverlayHostName("validator player " + name));

            OverlayEndpoint playerNewHost = new OverlayEndpoint(playerClient.addr, new OverlayHostName("agent player " + name));
            PlayerInfo playerInfo = new PlayerInfo(playerId, playerNewHost, validatorHost, name);

            NewPlayerProcess(playerInfo, new PlayerData(), lck);
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

        void OnNewWorldRequest(Point worldPos, WorldSerialized ser, int generation)
        {
            if (worlds.ContainsKey(worldPos))
            {
                Log.Dump(worldPos, "world alrady present");
                return;
            }

            if (!validatorPool.Any())
                throw new Exception("no validators!");

            ManualLock<Point> lck = new ManualLock<Point>(worldLocks, worldPos);

            if (!lck.Locked)
            {
                Log.Dump(worldPos, "can't work, locked");
                return;
            }

            string hostName = "host world " + worldPos;
            if(generation != 0)
                hostName = hostName + " (" + generation + ")";
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName(hostName));

            WorldInitializer init;
            WorldInfo info = new WorldInfo(worldPos, validatorHost, generation);
            bool hasSpawn;

            if (ser == null)
            {
                WorldSeed seed = new WorldSeed(r.Next(), RandomColor(worldPos));

                if (serverSpawnDensity == 0)
                {
                    if (worldPos == Point.Zero)
                        seed.hasSpawn = true;
                }
                else if ((worldPos.x % serverSpawnDensity == 0) && (worldPos.y % serverSpawnDensity == 0))
                {
                    seed.hasSpawn = true;
                }

                hasSpawn = seed.hasSpawn;
                init = new WorldInitializer(info, seed);
            }
            else
            {
                hasSpawn = ser.spawnPos.HasValue;
                init = new WorldInitializer(info, ser);
            }


            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);

            RemoteAction
                .Send(myHost, validatorClient, MessageType.WORLD_VALIDATOR_ASSIGN, init)
                .Respond(remoteActions, lck, (res, stm) =>
                {
                    if(res != Response.SUCCESS)
                        throw new Exception( Log.StDump("unexpected", res) );
                    
                    if (hasSpawn == true)
                        spawnWorlds.Add(worldPos);

                    worlds.Add(info.position, info);

                    //Log.LogWriteLine("New world " + worldPos + " validated by " + validatorHost.addr);

                    foreach (Point p in Point.SymmetricRange(Point.One))
                    {
                        if (p == Point.Zero)
                            continue;

                        Point neighborPos = p + info.position;

                        if (!worlds.ContainsKey(neighborPos))
                            continue;

                        WorldInfo neighborWorld = worlds[neighborPos];

                        myHost.ConnectSendMessage(neighborWorld.host, MessageType.NEW_NEIGHBOR, info);
                        myHost.ConnectSendMessage(info.host, MessageType.NEW_NEIGHBOR, neighborWorld);
                    }

                    //gameInfo.NET_AddWorld(info);
                    //myHost.BroadcastGroup(Client.hostName, MessageType.NEW_WORLD, info);
                });

            //myHost.SendMessage(validatorClient, MessageType.WORLD_VALIDATOR_ASSIGN, validatorId, init);

            //DelayedAction da = new DelayedAction()
            //{
            //    ep = validatorClient,
            //    a = () =>
            //    {
            //    }
            //};

            //delayedActions.Add(validatorId, da);

        }

        void OnSpawnRequest(Guid playerId)
        {
            MyAssert.Assert(players.ContainsKey(playerId));
            PlayerInfo inf = players[playerId];
            
            if (!spawnWorlds.Any())
            {
                Log.Dump("No spawn worlds", inf);
                return;
            }

            Point spawnWorldPos = spawnWorlds.Random(n => r.Next(n));

            WorldInfo spawnWorld = worlds.GetValue(spawnWorldPos);

            myHost.ConnectSendMessage(spawnWorld.host, MessageType.SPAWN_REQUEST, inf);
        }

        void OnStopValidating(Node n)
        {
            IPEndPoint addr = n.info.remote.addr;
            
            MyAssert.Assert(validatorPool.Contains(addr));
            validatorPool.Remove(addr);

            Log.Dump(n.info.remote);
        }

        void OnWorldHostDisconnect(WorldInitializer w)
        {
            //Log.Dump(mt, w.info);
            worlds.Remove(w.info.position);
            OnNewWorldRequest(w.info.position, w.world, w.info.generation + 1);
        }

        void OnPlayerHostDisconnect(PlayerInfo inf, PlayerData pd)
        {
            Log.Dump(inf, pd);
            OnNewPlayerRequest(inf, pd);
        }

        public void PrintStats()
        {
            myHost.PrintStats();
        }
    }
}
