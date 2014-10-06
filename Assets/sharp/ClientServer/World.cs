﻿using System;
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
    [Serializable]
    public class Tile
    {
        public bool solid = false;
        public bool loot = false;

        [XmlIgnoreAttribute]
        public Guid player = Guid.Empty;

        public string PlayerGuid
        {
            get
            {
                if (player != Guid.Empty)
                    return player.ToString();
                else
                    return "";
            }

            set
            {
                if (value != "")
                    player = new Guid(value);
                else
                    player = Guid.Empty;
            }
        }

        public bool IsEmpty() { return (player == Guid.Empty) && !solid; }

        public Tile() { }
    }

    [Serializable]
    public class WorldInitializer
    {
        public int seed;
        public double wallDensity;
        public double lootDensity;

        public WorldInitializer() { }
        internal WorldInitializer(int seed_, double wallDensity_ = .5, double lootDensity_ = .05)
        {
            seed = seed_;
            wallDensity = wallDensity_;
            lootDensity = lootDensity_;
        }
    }

    [Serializable]
    public class WorldInfo
    {
        public Point worldPos;
        public OverlayEndpoint host;

        public WorldInfo() { }
        public WorldInfo(Point worldPos_, OverlayEndpoint host_)
        {
            worldPos = worldPos_;
            host = host_;
        }

        public override string ToString()
        {
            return GetFullInfo();
        }

        public string GetFullInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("World pos: {0}\n", worldPos);
            sb.AppendFormat("Validator host: {0}\n", host);

            return sb.ToString();
        }
        public string GetShortInfo() { return "World " + worldPos.ToString(); }
    }

    public enum MoveValidity
    {
        VALID = 0,
        BOUNDARY = 1,
        OCCUPIED_PLAYER = 2,
        OCCUPIED_WALL = 4,
        TELEPORT = 8,
        NEW = 16
    };

    [Serializable]
    public class WorldSerialized
    {
        public Plane<Tile> map;
        public WorldInfo myInfo;
    }
    
    class World
    {
        static readonly Point worldSize = new Point(20, 10);

        public readonly WorldInfo info;
        public Plane<Tile> map;

        public Point Position { get { return info.worldPos; } }

        public Dictionary<Guid, Point> playerPositions = new Dictionary<Guid,Point>();
        GameInfo gameInfo;

        public Action<PlayerInfo> onLootHook = (info) => { };
        public Action<PlayerInfo, MoveType> onMoveHook = (a,b) => { };

        public World(WorldSerialized ws, GameInfo info_)
        {
            info = ws.myInfo;
            gameInfo = info_;
            map = ws.map;

            foreach (Point p in Point.Range(map.Size))
                if (map[p].player != Guid.Empty)
                    playerPositions.Add(map[p].player, p);
        }
        public World(WorldInfo myInfo_, WorldInitializer init, GameInfo info_)
        {
            info = myInfo_;
            gameInfo = info_;

            map = new Plane<Tile>(worldSize);

            foreach (Point p in Point.Range(map.Size))
                map[p] = new Tile();

            Generate(init);
        }

        public WorldSerialized Serialize()
        {
            return new WorldSerialized() { map = map, myInfo = info };
        }

        public IEnumerable<Point> GetBoundary()
        {
            HashSet<Point> bnd = new HashSet<Point>();

            for (int x = 0; x < map.Size.x; ++x)
            {
                bnd.Add(new Point(x, 0));
                bnd.Add(new Point(x, map.Size.y - 1));
            }

            for (int y = 0; y < map.Size.y; ++y)
            {
                bnd.Add(new Point(0, y));
                bnd.Add(new Point(map.Size.x - 1, y));
            }

            return from p in bnd
                   orderby p.ToString()
                   select p;
        }
        public IEnumerable<KeyValuePair<Point, Tile>> GetSpawn()
        {
            foreach (Point p in Point.Range(map.Size))
            {
                Tile t = map[p];
                if (t.IsEmpty() && t.loot == false)
                    yield return new KeyValuePair<Point, Tile>(p, t);
            }
        }

        void Generate(WorldInitializer init)
        {
            ServerClient.MyRandom seededRandom = new ServerClient.MyRandom(init.seed);

            // random terrain
            foreach (Tile t in map.GetTiles())
            {
                if (seededRandom.NextDouble() < init.wallDensity)
                    t.solid = true;
                else
                {
                    t.solid = false;

                    if (seededRandom.NextDouble() < init.lootDensity)
                        t.loot = true;
                }
            }

            /*/ clear spawn points
            foreach (Point bp in world.GetBoundary())
            {
                Tile t = world.map[bp];

                //t.solid = false;
                //t.loot = false;
                //MyAssert.Assert(t.IsEmpty());
            }
             */

        }
        public void ConsoleOut()
        {
            Plane<char> pic = new Plane<char>(map.Size);

            foreach (var kv in map.GetEnum())
            {
                Point p = kv.Key;
                Tile t = kv.Value;

                if (t.solid)
                    pic[p] = '*';
                else if (t.player != Guid.Empty)
                    pic[p] = gameInfo.GetPlayerById(t.player).name[0];
                else if (t.loot)
                    pic[p] = '$';
            }

            for (int y = map.Size.y - 1; y >= 0; --y)
            {
                for (int x = 0; x < map.Size.x; ++x)
                    Console.Write(pic[x, y]);
                Console.WriteLine();
            }
        }
        
        public void AddPlayer(Guid player, Point pos)
        {
            MyAssert.Assert(!playerPositions.ContainsKey(player));
            Move(player, pos, MoveValidity.NEW);
        }
        public void RemovePlayer(Guid player)
        {
            Point pos = playerPositions.GetValue(player);
            MyAssert.Assert(map[pos].player == player);
            map[pos].player = Guid.Empty;

            playerPositions.Remove(player);
        }

        public MoveValidity CheckValidMove(Guid player, Point newPos)
        {
            MoveValidity mv = MoveValidity.VALID;
            
            Point curPos = playerPositions.GetValue(player);

            Point diff = newPos - curPos;
            if (Math.Abs(diff.x) > 1)
                mv |= MoveValidity.TELEPORT;
            if (Math.Abs(diff.y) > 1)
                mv |= MoveValidity.TELEPORT; 
            
            Tile tile;
            try
            {
                tile = map[newPos];
            }
            catch (IndexOutOfRangeException)
            {
                mv |= MoveValidity.BOUNDARY;
                return mv;
            }

            if (!tile.IsEmpty())
            {
                if (tile.player != Guid.Empty)
                    mv |= MoveValidity.OCCUPIED_PLAYER;
                if (tile.solid)
                    mv |= MoveValidity.OCCUPIED_WALL;
            }

            return mv;
        }
        public void Move(Guid player, Point newPos, MoveValidity mv = MoveValidity.VALID)
        {
            PlayerInfo p = gameInfo.GetPlayerById(player);

            if (mv != MoveValidity.NEW)
            {
                Point curPos = playerPositions.GetValue(player);

                MoveValidity v = CheckValidMove(player, newPos) & ~mv;
                if (v != MoveValidity.VALID)
                    Log.LogWriteLine("Game.Move Warning: Invalid move {0} from {1} to {2} by {3}",
                        v, curPos, newPos, p.name);

                MyAssert.Assert(map[curPos].player == player);
                map[curPos].player = Guid.Empty;
            }

            Tile tile = map[newPos];
            MyAssert.Assert(tile.IsEmpty());

            if (tile.loot == true)
                onLootHook(p);
            
            playerPositions[player] = newPos;
            tile.player = player;
            tile.loot = false;

            MoveType mt = mv == MoveValidity.NEW ? MoveType.JOIN : MoveType.MOVE;
            
            onMoveHook(p, mt);
        }

        int BoundaryMove(ref int p, int sz)
        {
            int ret = 0;

            while (p < 0)
            {
                p += sz;
                ret--;
            }
            
            while(p >= sz)
            {
                p -= sz;
                ret++;
            }

            return ret;
        }
        public Point BoundaryMove(ref Point p)
        {
            int px = p.x;
            int py = p.y;

            Point ret = new Point(BoundaryMove(ref px, map.Size.x), BoundaryMove(ref py, map.Size.y));

            p = new Point(px, py);

            return Position + ret;
        }
        
    }

    class WorldValidator
    {
        Random rand = new Random();

        World world;
        Action<Action> sync;
        OverlayHost myHost;

        GameInfo gameInfo;
        OverlayEndpoint serverHost;

        Dictionary<Guid, Action<MessageType>> playerLocks = new Dictionary<Guid, Action<MessageType>>();
        void FinishLock(Guid id, MessageType mt)
        {
            var act = playerLocks.GetValue(id);
            
            playerLocks.Remove(id);
            
            act.Invoke(mt);
        }

        public WorldValidator(WorldInfo info, WorldInitializer init, Action<Action> sync_, GlobalHost globalHost, GameInfo gameInfo_, OverlayEndpoint serverHost_)
        {
            gameInfo = gameInfo_;
            sync = sync_;
            serverHost = serverHost_;

            world = new World(info, init, gameInfo);

            myHost = globalHost.NewHost(info.host.hostname, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;
        }
        
        Node.MessageProcessor AssignProcessor(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)
                return (mt, stm, nd) => { throw new Exception("WorldValidator not expecting messages from Client." + mt + " " + nd.info); };
            if (remoteName == Server.hostName)
                return (mt, stm, nd) => { throw new Exception("WorldValidator not expecting messages from Server." + mt + " " + nd.info); };
            
            NodeRole role = gameInfo.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.PLAYER_VALIDATOR)
            {
                PlayerInfo inf = gameInfo.GetPlayerByHost(n.info.remote);
                return (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd, inf);
            }
            
            if (role == NodeRole.PLAYER)
            {
                PlayerInfo inf = gameInfo.GetPlayerByHost(n.info.remote);
                return (mt, stm, nd) => ProcessPlayerMessage(mt, stm, nd, inf);
            }

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = gameInfo.GetWorldByHost(n.info.remote);
                return (mt, stm, nd) => ProcessWorldValidatorMessage(mt, stm, nd, inf);
            }

            throw new InvalidOperationException("WorldValidator.AssignProcessor unexpected connection " + n.info.remote + " " + role);
        }
        
        void ProcessNewConnection(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;

            if (remoteName == Client.hostName)
                OnNewClient(n);
        }

        void OnNewClient(Node n)
        {
            n.SendMessage(MessageType.WORLD_INIT, world.Serialize());
        }

        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.SPAWN_FAIL || mt == MessageType.SPAWN_SUCCESS)
                sync.Invoke(() => FinishLock(inf.id, mt));
            else
                throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessWorldValidatorMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.REALM_MOVE_FAIL || mt == MessageType.REALM_MOVE_SUCCESS)
            {
                Guid id = Serializer.Deserialize<Guid>(stm);
                sync.Invoke(() => FinishLock(id, mt));
            }
            else
                throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
        }
        void ProcessPlayerMessage(MessageType mt, Stream stm, Node n, PlayerInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
                sync.Invoke(() => OnSpawnRequest(inf));
            else if (mt == MessageType.MOVE)
            {
                Point newPos = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnMoveValidate(inf, newPos));
            }
            else
                throw new Exception("WorldValidator.ProcessClientMessage bad message type " + mt.ToString());
        }

        void OnSpawnRequest(PlayerInfo inf)
        {
            if(playerLocks.ContainsKey(inf.id))
            {
                Log.LogWriteLine("Spawn failed, spawn in progress.\n World {0} \n Player {1}", world.info, inf);
                return;
            }
            
            myHost.ConnectSendMessage(inf.validatorHost, MessageType.SPAWN_REQUEST);

            playerLocks.Add(inf.id, (mt) =>
                {
                    if (mt == MessageType.SPAWN_FAIL)
                    {
                        Log.LogWriteLine("Spawn failed, already spawned.\n World {0} \n Player {1}", world.info, inf);
                    }
                    else if (mt == MessageType.SPAWN_SUCCESS)
                    {
                        var spawn = world.GetSpawn().ToList();
                        if (!spawn.Any())
                        {
                            throw new Exception("Spawn failed, no space");
                            //Log.LogWriteLine("Spawn failed, no space.\n World {0} \n Player {1}", world.worldInfo, inf);
                            //return;
                        }

                        Point spawnPos = spawn.Random((n) => rand.Next(n)).Key;

                        if (!world.playerPositions.Any())   // on first spawn
                            BoundaryRequest();
                        
                        world.AddPlayer(inf.id, spawnPos);
                        myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_JOIN, inf.id, spawnPos);
                        myHost.ConnectSendMessage(inf.validatorHost, MessageType.PLAYER_JOIN);
                    }
                    else
                        throw new Exception("Unexpected response in OnSpawnRequest " + mt);
                });
        }
        void OnMoveValidate(PlayerInfo inf, Point newPos)
        {
            if (playerLocks.ContainsKey(inf.id))
            {
                Log.LogWriteLine("World {0}: {1} can't move, locked", world.info.GetShortInfo(), inf.GetShortInfo());
                return;
            }

            if (!world.playerPositions.ContainsKey(inf.id))
            {
                Log.LogWriteLine("World {0}: Invalid move {1} by {2}: player absent from this world",
                    world.info.GetShortInfo(), newPos, inf.GetShortInfo());
                return;
            }
            
            Point currPos = world.playerPositions.GetValue(inf.id);

            MoveValidity v = world.CheckValidMove(inf.id, newPos);
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("World {4}: Invalid move {0} from {1} to {2} by {3}", v,
                    currPos, newPos, inf.GetShortInfo(), world.info.GetShortInfo());
                return;
            }

            world.Move(inf.id, newPos, MoveValidity.VALID);

            myHost.BroadcastGroup(Client.hostName, MessageType.MOVE, inf.id, newPos);
            //Broadcast(MessageType.MOVE, myId, p.id, newPos);
        }
        public void OnValidateRealmMove(PlayerInfo player, Point currPos, Point newPos)
        {
            if (playerLocks.ContainsKey(player.id))
            {
                Log.LogWriteLine(Log.Dump(this, world.info, player, "can't move, locked"));
                return;
            }

            MoveValidity v = world.CheckValidMove(player.id, newPos);

            if (v != MoveValidity.BOUNDARY)
            {
                Log.LogWriteLine(Log.Dump(this, world.info, player, v, currPos, newPos, "invalid move"));
                return;
            }

            Point currentRealmPos = world.Position;
            Point targetRealmPos = world.BoundaryMove(ref newPos);

            MyAssert.Assert(currentRealmPos != targetRealmPos);
            WorldInfo targetRealm = gameInfo.TryGetWorldByPos(targetRealmPos);
            if (targetRealm == null)
            {
                Log.LogWriteLine(Log.Dump(this, world.info, player, currentRealmPos, targetRealmPos, newPos, "no realm to move in"));
                return;
            }

            Log.LogWriteLine(Log.Dump(this, world.info, player, currentRealmPos, targetRealmPos, newPos, "realm request"));

            myHost.ConnectSendMessage(targetRealm.host, MessageType.REALM_MOVE, player.id, newPos);

            playerLocks.Add(player.id, (mt) =>
            {
                if (mt == MessageType.REALM_MOVE_FAIL)
                {
                    Log.LogWriteLine("World {2}: Realm move failed {0} for {1}", v,
                        player.GetShortInfo(), world.info.GetShortInfo());
                }
                else if (mt == MessageType.REALM_MOVE_SUCCESS)
                {
                    Log.LogWriteLine("World {1}: Realm move success for {0}",
                        player.GetShortInfo(), world.info.GetShortInfo());

                    world.RemovePlayer(player.id);
                    myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_LEAVE, player.id);
                }
                else
                    throw new Exception("Unexpected response in OnValidateRealmMove " + mt);
            });

        }

        public void OnRealmMove(PlayerInfo player, Point newPos, Node n)
        {
            if (playerLocks.ContainsKey(player.id))
            {
                Log.LogWriteLine(Log.Dump(this, world.info, player, "can't join, locked"));
                return;
            }

            Tile t = world.map[newPos];

            if (!t.IsEmpty())
            {
                MoveValidity mv = MoveValidity.VALID;

                if (t.player != Guid.Empty)
                    mv = MoveValidity.OCCUPIED_PLAYER;
                else if (t.solid)
                    mv = MoveValidity.OCCUPIED_WALL;
                else
                    throw new Exception(Log.Dump(this, world.info, player, mv, "bad tile status"));

                Log.LogWriteLine(Log.Dump(this, world.info, player, mv, "can't join, blocked"));

                n.SendMessage(MessageType.REALM_MOVE_FAIL, player.id);

                return;
            }

                n.SendMessage(MessageType.REALM_MOVE_SUCCESS, player.id);

            world.AddPlayer(player.id, newPos);
            myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_JOIN, player.id);
        }

        void BoundaryRequest()
        {
            Point myPosition = world.Position;
            foreach (Point delta in Point.SymmetricRange(new Point(1, 1)))
            {
                Point newPos = myPosition + delta;
                
                if(gameInfo.GetWorldByPos(newPos) == null)
                    myHost.ConnectSendMessage(serverHost, MessageType.NEW_WORLD, newPos);
            }
        }
    }
}
