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
    /*class Aggregate
    {
        public static System.Random r = new System.Random();

        public ActionSyncronizer sync;
        public NodeCollection peers;

        MessageTypeManager manager = new MessageTypeManager();
        public AssignmentInfo gameAssignments = new AssignmentInfo();

        MessageProcessor messager;

        public Game game;

        public Aggregate()
        {
            sync = new ActionSyncronizer();
            peers = new NodeCollection(sync.GetAsDelegate(), ProcessMessage, OnNewConnection);

            messager = new MessageProcessor(manager, gameAssignments, sync);
        }

        public static IPEndPoint ParseParamForIP(List<string> param)
        {
            IPAddress ip = NetTools.GetMyIP();
            int port = NodeCollection.nStartPort;

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
        public bool Connect(IPEndPoint ep, bool mesh = false)
        {
            Node n = peers.TryConnectAsync(ep);

            if (n == null)
                return false;


            if (mesh)
            {
                SendIpTable(n);
                n.SendMessage(MessageType.TABLE_REQUEST);
            }

            return true;
        }
        public void ParamConnect(List<string> param, bool mesh = false)
        {
            IPEndPoint ep = ParseParamForIP(param);
            Log.LogWriteLine("Connecting to {0} {1}", ep.Address, ep.Port);
            if (!Connect(ep, mesh))
                Log.LogWriteLine("Already connected/connecting");
        }
        
        void ProcessMessage(Node n, Stream stm, MessageType mt)
        {
            if (mt == MessageType.TABLE_REQUEST)
            {
                sync.Add(() => SendIpTable(n));
            }
            else if (mt == MessageType.TABLE)
            {
                var table = Serializer.Deserialize<IPEndPointSer[]>(stm);
                sync.Add(() => OnIpTable(table));
            }
            else if (mt == MessageType.ROLE)
            {
                var role = Serializer.Deserialize<Role>(stm);
                sync.Add(() => OnRole(n, role));
            }
            else if (mt == MessageType.GENERATE)
            {
                var init = Serializer.Deserialize<GameInitializer>(stm);
                sync.Add(() => OnGenerate(init));
            }
            else if (manager.messages.ContainsKey(mt))
            {
                messager.ProcessMessage(n, mt, stm);
            }
            else
            {
                throw new InvalidOperationException("Unexpected message type " + mt.ToString());
            }
        }

        void SendIpTable(Node n)
        {
            var a = from nd in peers.GetAllNodes()
                    select new IPEndPointSer(nd.Address);
            n.SendMessage(MessageType.TABLE, (object)a.ToArray());
        }
        void OnIpTable(IPEndPointSer[] table)
        {
            foreach (var ip in table)
                peers.TryConnectAsync(ip.Addr);
        }
        void OnRole(Node n, Role r)
        {
            foreach (Guid id in r.player.Concat(r.validator))
                gameAssignments.nodes[id] = n;

            gameAssignments.AddRole(r);
        }
        void OnNewConnection(Node n)
        {
            n.SendMessage(MessageType.ROLE, gameAssignments.GetMyRole());
        }
        void OnGenerate(GameInitializer init)
        {
            if (game != null)
            {
                Log.LogWriteLine("GenerateGame: game already generated!");
                return;
            }

            game = new Game(init, gameAssignments.GetAllRoles());

            foreach (Player pl in game.players.Values)
            {
                gameAssignments.validators.Add(pl.id, pl.validator);
                gameAssignments.roles.Add(pl.validator, NodeRole.PLAYER_VALIDATOR);
            }

            foreach (World w in game.AllWorlds())
            {
                gameAssignments.validators.Add(w.id, w.validator);
                gameAssignments.roles.Add(w.validator, NodeRole.WORLD_VALIDATOR);
            }

            manager.playerMessageReciever.pa = new PlayerActor(game, gameAssignments);

            Role myRole = gameAssignments.GetMyRole();
            
            if (myRole.player.Any())
            {
                StringBuilder sb = new StringBuilder("My player #'s: ");
                sb.Append(String.Join(" ", (from id in myRole.player select game.players[id].ShortName).ToArray()));
                Log.LogWriteLine("{0}", sb.ToString());
            }

            var validatedPlayers = (from pl in game.players.Values
                                    where gameAssignments.IsMyRole(pl.validator)
                                    select pl).ToArray();

            if (validatedPlayers.Any())
            {
                StringBuilder sb = new StringBuilder("validating for players: ");
                sb.Append(String.Join(" ", (from pl in validatedPlayers select pl.ShortName).ToArray()));
                Log.LogWriteLine("{0}", sb.ToString());
            }

            var validatedWorlds = (from w in game.AllWorlds()
                                    where gameAssignments.IsMyRole(w.validator)
                                    select w).ToArray();

            if (validatedWorlds.Any())
            {
                StringBuilder sb = new StringBuilder("validating for worlds: ");
                sb.Append(String.Join(" ", (from w in validatedWorlds select w.worldPosition.ToString()).ToArray()));
                Log.LogWriteLine("{0}", sb.ToString());
            }

            Action<MessageType, object[]> broadcaster = (mt, arr) => Broadcast(mt, arr);
          
            foreach (Player p in validatedPlayers)
                manager.playerValidatorMessageReciever.validators.Add(p.id,
                    new PlayerValidator(p.id, gameAssignments, broadcaster, p.FullName));

            Dictionary<Point, Guid> worldsByPoint = new Dictionary<Point, Guid>();

            foreach (World w in game.AllWorlds())
                worldsByPoint.Add(w.worldPosition, w.id);
            
            foreach (World w in validatedWorlds)
                manager.playerWorldVerifierMessageReciever.validators.Add(w.id,
                    new WorldValidator(w.id, gameAssignments, broadcaster, init, worldsByPoint));

            game.ConsoleOut();
        }

        public void AddMyRole(Role r)
        {
            foreach (Guid id in r.player.Concat(r.validator))
                gameAssignments.controlledByMe.Add(id);
            
            gameAssignments.AddRole(r);

            Broadcast(MessageType.ROLE, r);
        }
 
        public void Broadcast(MessageType mt, params object[] messages)
        {
            foreach (var n in peers.GetAllNodes())
                n.SendMessage(mt, messages);
        }
        public void GenerateGame()
        {
            if (game != null)
            {
                Log.LogWriteLine("GenerateGame: game already generated!");
                return;
            }

            GameInitializer init = new GameInitializer(System.DateTime.Now.Millisecond, gameAssignments.GetAllRoles());
            Broadcast(MessageType.GENERATE, init);
        }
        public void Move(Player p, Point newPos, MessageType mt)
        {
            World w = game.GetPlayerWorld(p.id);
            gameAssignments.SendValidatorMessage(mt, p.id, w.id, newPos);
        }

        public void SetMoveHook(Action<World, Player, MoveType> hook)
        { manager.playerMessageReciever.pa.onMoveHook = hook; }
    }
    */
    /*class Validator
    {
        protected Guid myId;
        protected AssignmentInfo gameInfo;

        public Validator(Guid myId_, AssignmentInfo gameInfo_, Action<MessageType, object[]> broadcaster_)
        {
            myId = myId_;
            gameInfo = gameInfo_;
            broadcaster = broadcaster_;
        }

        Action<MessageType, object[]> broadcaster;
        protected void Broadcast(MessageType mt, params object[] objs) { broadcaster.Invoke(mt, objs); }
    }*/

    /*class WorldValidatorProcessor : MessageReceiver
    {
        public Dictionary<Guid, WorldValidator> validators = new Dictionary<Guid,WorldValidator>();

        public override void ProcessMessage(MessageType mt, Guid sender, Guid receiver, Stream stm, Action<Action> syncronizer)
        {
            Point newPos = new Point();
            if(mt == MessageType.VALIDATE_MOVE || mt == MessageType.VALIDATE_TELEPORT || mt == MessageType.VALIDATE_REALM_MOVE)
                newPos = Serializer.Deserialize<Point>(stm);


            Guid player = Guid.Empty;
            if(mt == MessageType.REALM_MOVE)
            {
                player = Serializer.Deserialize<Guid>(stm);
                newPos = Serializer.Deserialize<Point>(stm);
            }

            if(mt == MessageType.REALM_MOVE_FAIL || mt == MessageType.REALM_MOVE_SUCESS)
                player = Serializer.Deserialize<Guid>(stm);

            MoveValidity mv = MoveValidity.VALID;
            if(mt == MessageType.REALM_MOVE_FAIL)
                mv = Serializer.Deserialize<MoveValidity>(stm);

            syncronizer.Invoke(() =>
            {
                WorldValidator wv = validators.GetValue(receiver);

                if (mt == MessageType.REALM_MOVE)
                {
                    wv.OnRealmMove(sender, player, newPos);
                    return;
                }
                
                Player p;
                if (mt == MessageType.REALM_MOVE_FAIL || mt == MessageType.REALM_MOVE_SUCESS)
                    p = wv.validatorWorld.players.GetValue(player);
                else
                    p = wv.validatorWorld.players.GetValue(sender);

                Point currPos = wv.validatorWorld.playerPositions.GetValue(p.id);

                if (mt == MessageType.VALIDATE_MOVE)
                    wv.OnValidateMove(p, currPos, newPos);
                else if (mt == MessageType.VALIDATE_TELEPORT)
                    wv.OnValidateTeleport(p, currPos, newPos);
                else if (mt == MessageType.VALIDATE_REALM_MOVE)
                    wv.OnValidateRealmMove(p, currPos, newPos, false);
                else if (mt == MessageType.FREEZING_SUCCESS)
                    wv.OnFreezeSuccess(p, currPos);
                else if (mt == MessageType.FREEZING_FAIL)
                    wv.OnFreezeFail(p, currPos);
                else if (mt == MessageType.REALM_MOVE_FAIL)
                    wv.OnRealmMoveFail(p, mv);
                else if (mt == MessageType.REALM_MOVE_SUCESS)
                    wv.OnRealmMoveSuccess(p);
                else
                    throw new Exception("WorldValidatorProcessor: Unsupported message " + mt.ToString());
            });
        }
    }*/

    /*class MoveLock
    {
        public Point newPos;
        public bool teleport;

        public MoveLock(Point newPos_, bool teleport_ = false)
        {
            newPos = newPos_;
            teleport = teleport_;
        }
    }*/
    
    /*class WorldValidator : Validator
    {
        Dictionary<Point, Guid> worldByPoint;
        internal World validatorWorld;
        Dictionary<Guid, MoveLock> movementLocks = new Dictionary<Guid,MoveLock>();

        public WorldValidator(Guid myId_, AssignmentInfo gameInfo_,
            Action<MessageType, object[]> broadcaster_, GameInitializer init,
            Dictionary<Point, Guid> worldByPoint_)
            : base(myId_, gameInfo_, broadcaster_)
        {
            worldByPoint = worldByPoint_;

            Game tempGame = new Game(init, gameInfo.GetAllRoles());
            validatorWorld = tempGame.GetWorld(myId_);

            validatorWorld.onLootHook = OnLootPickup;
        }

        public void ProcessPlayerMessage(MessageType mt, Stream stm, Action<Action> syncronizer, Guid playerId)
        {
            Point newPos = Serializer.Deserialize<Point>(stm);

            syncronizer.Invoke(() =>
            {
                Player p = validatorWorld.players.GetValue(playerId);

                Point currPos = validatorWorld.playerPositions.GetValue(p.id);

                if (mt == MessageType.VALIDATE_MOVE)
                    OnValidateMove(p, currPos, newPos);
                else if (mt == MessageType.VALIDATE_TELEPORT)
                    OnValidateTeleport(p, currPos, newPos);
                else if (mt == MessageType.VALIDATE_REALM_MOVE)
                    OnValidateRealmMove(p, currPos, newPos, false);
                else
                    throw new Exception("WorldValidatorProcessor ProcessPlayerMessage: Unsupported message " + mt.ToString());
            });
        }
        //public void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Action<Action> syncronizer

        void OnLootPickup(Player p)
        {
            gameInfo.SendValidatorMessage(MessageType.LOOT_PICKUP_BROADCAST, myId, p.id);
        }
        void TeleportCleanup(bool success, Guid player)
        {
            if(success)
                gameInfo.SendValidatorMessage(MessageType.CONSUME_FROZEN_ITEM, myId, player);
            else
                gameInfo.SendValidatorMessage(MessageType.UNFREEZE_ITEM, myId, player);
        }

        public void OnValidateMove(Player p, Point currPos, Point newPos)
        {
            if (movementLocks.ContainsKey(p.id))
            {
                Log.LogWriteLine("Validator: {0} can't move, locked", p.FullName);
                return;
            }

            MoveValidity v = validatorWorld.CheckValidMove(p.id, newPos);
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("Validator: Invalid move {0} from {1} to {2} by {3}", v,
                    currPos, newPos, p.FullName);
                return;
            }

            validatorWorld.Move(p.id, newPos, MoveValidity.VALID);

            Broadcast(MessageType.MOVE, myId, p.id, newPos);
        }
        public void OnValidateTeleport(Player p, Point currPos, Point newPos)
        {
            if (movementLocks.ContainsKey(p.id))
            {
                Log.LogWriteLine("Validator: {0} can't teleport, locked", p.FullName);
                return;
            }

            MoveValidity v = validatorWorld.CheckValidMove(p.id, newPos) & ~(MoveValidity.TELEPORT | MoveValidity.BOUNDARY);
            
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("Validator: Invalid (step 1) teleport {0} from {1} to {2} by {3}", v, currPos, newPos, p.FullName);
                return;
            }
            

            Log.LogWriteLine("Validator: Freezing request for teleport from {1} to {2} by {3}", v, currPos, newPos, p.FullName);

            movementLocks.Add(p.id, new MoveLock(newPos));
            gameInfo.SendValidatorMessage(MessageType.FREEZE_ITEM, myId, p.id);
        }
        public bool OnValidateRealmMove(Player p, Point currPos, Point newPos, bool teleporting)
        {
            if (movementLocks.ContainsKey(p.id))
            {
                Log.LogWriteLine("Validator: {0} can't realm move, locked", p.FullName);
                return false;
            }

            MoveValidity v = validatorWorld.CheckValidMove(p.id, newPos);

            if (teleporting)
                v &= ~MoveValidity.TELEPORT;
            
            if (v != MoveValidity.BOUNDARY)
            {
                Log.LogWriteLine("Validator: Invalid realm move {0} from {1} to {2} by {3}", v, currPos, newPos, p.FullName);
                return false;
            }

            Point currentRealm = validatorWorld.worldPosition;
            Point targetRealm = validatorWorld.BoundaryMove(ref newPos);

            MyAssert.Assert(!currentRealm.Equals(targetRealm));
            if (!worldByPoint.ContainsKey(targetRealm))
            {
                Log.LogWriteLine("Validator: No realm for realm move from {0} to {1},{2} by {3}",
                    currentRealm, targetRealm, newPos, p.FullName);
                return false;
            }


            Log.LogWriteLine("Validator: Request for realm move from {0} to {1},{2} by {3}",
                currentRealm, targetRealm, newPos, p.FullName);

            movementLocks.Add(p.id, new MoveLock(newPos, teleporting));

            gameInfo.SendValidatorMessage(MessageType.REALM_MOVE, myId, worldByPoint[targetRealm], p.id, newPos);

            return true;
        }

        public void OnRealmMove(Guid world, Guid player, Point newPos)
        {
            Tile t = validatorWorld.map[newPos];

            if (!t.IsEmpty())
            {
                MoveValidity mv = MoveValidity.VALID;

                if (t.player != Guid.Empty)
                    mv = MoveValidity.OCCUPIED_PLAYER;
                else if (t.solid)
                    mv = MoveValidity.OCCUPIED_WALL;
                else
                    MyAssert.Assert(false);

                gameInfo.SendValidatorMessage(MessageType.REALM_MOVE_FAIL, myId, world, player, mv);

                return;
            }

            gameInfo.SendValidatorMessage(MessageType.REALM_MOVE_SUCESS, myId, world, player);

            validatorWorld.AddPlayer(player, newPos);
            Broadcast(MessageType.ADD_PLAYER, myId, player, newPos);
        }
        public void OnRealmMoveSuccess(Player p)
        {
            MyAssert.Assert(movementLocks.ContainsKey(p.id));
            
            if (movementLocks[p.id].teleport)
                TeleportCleanup(true, p.id);
            
            movementLocks.Remove(p.id);

            Log.LogWriteLine("Validator: Realm move success for {0}", p.FullName);

            validatorWorld.RemovePlayer(p.id);
            Broadcast(MessageType.REMOVE_PLAYER, myId, p.id);
        }
        public void OnRealmMoveFail(Player p, MoveValidity mv)
        {
            MyAssert.Assert(movementLocks.ContainsKey(p.id));

            if (movementLocks[p.id].teleport)
                TeleportCleanup(false, p.id);

            movementLocks.Remove(p.id);

            Log.LogWriteLine("Validator: Realm move failed {0} for {1}", mv, p.FullName);
        }

        public void OnFreezeSuccess(Player p, Point currPos)
        {
            MyAssert.Assert(movementLocks.ContainsKey(p.id));
            Point newPos = movementLocks[p.id].newPos;
            movementLocks.Remove(p.id);

            Log.LogWriteLine("Validator: Freeze sucessful. Trying to teleport from {0} to {1} by {2}.", currPos, newPos, p.FullName);

            MoveValidity v = validatorWorld.CheckValidMove(p.id, newPos) & ~MoveValidity.TELEPORT;

            if (v != MoveValidity.VALID)
            {
                if (v == MoveValidity.BOUNDARY)
                {
                    Log.LogWriteLine("Validator: requesting realm teleport");
                    // requesting intra-realm teleport
                    bool realmMoveRequestSuccess = OnValidateRealmMove(p, currPos, newPos, true);
                    if (!realmMoveRequestSuccess)
                    {
                        // request denied
                        Log.LogWriteLine("Validator: Invalid teleport {0} from {1} to {2} by {3}; realm move request failed", v, currPos, newPos, p.FullName);
                        TeleportCleanup(false, p.id);
                    }
                }
                else
                {
                    // teleporting fail
                    Log.LogWriteLine("Validator: Invalid (step 2) teleport {0} from {1} to {2} by {3}", v, currPos, newPos, p.FullName);
                    TeleportCleanup(false, p.id);
                }
            }
            else // teleporting success
            {
                validatorWorld.Move(p.id, newPos, MoveValidity.TELEPORT);

                TeleportCleanup(true, p.id);

                Broadcast(MessageType.TELEPORT, myId, p.id, newPos);
            }
        }
        public void OnFreezeFail(Player p, Point currPos)
        {
            MyAssert.Assert(movementLocks.ContainsKey(p.id));
            Point newPos = movementLocks[p.id].newPos;
            movementLocks.Remove(p.id);

            Log.LogWriteLine("Validator: Freeze failed. Was trying to teleport from {0} to {1} by {2}.", currPos, newPos, p.FullName);
        }
    }*/

    /*class PlayerValidatorProcessor : MessageReceiver
    {
        public Dictionary<Guid, PlayerValidator> validators = new Dictionary<Guid,PlayerValidator>();

        public override void ProcessMessage(MessageType mt, Guid sender, Guid receiver, Stream stm, Action<Action> syncronizer)
        {
            syncronizer.Invoke(() =>
                {
                    PlayerValidator pv = validators.GetValue(receiver);
                    if (mt == MessageType.LOOT_PICKUP_BROADCAST)
                        pv.OnLootBroadcast();
                    else if (mt == MessageType.FREEZE_ITEM)
                        pv.OnFreezeItem(sender);
                    else if (mt == MessageType.UNFREEZE_ITEM)
                        pv.OnUnfreeze();
                    else if (mt == MessageType.CONSUME_FROZEN_ITEM)
                        pv.OnConsumeFrozen();
                    else
                        throw new Exception("PlayerValidatorProcessor: Unsupported message " + mt.ToString());
                });
        }
    }*/

    /*class PlayerValidator : Validator
    {
        Inventory validatorInventory = new Inventory();
        string name;

        public PlayerValidator(Guid myId_, AssignmentInfo gameInfo_, Action<MessageType, object[]> broadcaster_,
            string name_) : base(myId_, gameInfo_, broadcaster_)
        {
            name = name_;
        }

        public void OnLootBroadcast()
        {
            validatorInventory.teleport++;
            //Log.LogWriteLine("{0} pick up teleport, now has {1} (validator)", name, validatorInventory.teleport);

            Broadcast(MessageType.LOOT_PICKUP, myId, myId);
        }
        public void OnFreezeItem(Guid worldId)
        {
            if (validatorInventory.teleport > 0)
            {
                validatorInventory.teleport--;
                validatorInventory.frozenTeleport++;

                gameInfo.SendValidatorMessage(MessageType.FREEZING_SUCCESS, myId, worldId);

                Log.LogWriteLine("{0} freezes one teleport (validator)", name);
            }
            else
            {
                gameInfo.SendValidatorMessage(MessageType.FREEZING_FAIL, myId, worldId);

                Log.LogWriteLine("{0} freeze failed (validator)", name);
            }
        }
        public void OnUnfreeze()
        {
            MyAssert.Assert(validatorInventory.frozenTeleport > 0);
            validatorInventory.teleport++;
            validatorInventory.frozenTeleport--;

            Log.LogWriteLine("{0} unfreezes one teleport (validator)", name);
        }
        public void OnConsumeFrozen()
        {
            MyAssert.Assert(validatorInventory.frozenTeleport > 0);
            validatorInventory.frozenTeleport--;

            Log.LogWriteLine("{0} consume teleport, now has {1} (validator)", name, validatorInventory.teleport);
            Broadcast(MessageType.LOOT_CONSUMED, myId, myId);
        }
    }*/

    /*class PlayerActorProcessor : MessageReceiver
    {
        public PlayerActor pa = null;

        public override void ProcessMessage(MessageType mt, Guid sender, Guid receiver, Stream stm, Action<Action> syncronizer)
        {
            if (mt == MessageType.MOVE || mt == MessageType.TELEPORT ||
                mt == MessageType.ADD_PLAYER || mt == MessageType.REMOVE_PLAYER)
            {
                Point newPos = new Point();
                if(mt != MessageType.REMOVE_PLAYER)
                    newPos = Serializer.Deserialize<Point>(stm);

                syncronizer.Invoke(() =>
                {
                    Player p = pa.game.players.GetValue(receiver);
                    World w = pa.game.GetWorld(sender);

                    if (mt == MessageType.MOVE)
                        pa.OnMove(w, p, newPos);
                    else if (mt == MessageType.TELEPORT)
                        pa.OnTeleport(w, p, newPos);
                    else if(mt == MessageType.ADD_PLAYER)
                        pa.OnAddPlayer(w, p, newPos);
                    else if (mt == MessageType.REMOVE_PLAYER)
                        pa.OnRemovePlayer(w, p);
                    else
                        throw new Exception("PlayerActorProcessor: Unsupported message " + mt.ToString());
                });
            }
            else
            {
                syncronizer.Invoke(() =>
                {
                    Player p = pa.game.players.GetValue(receiver);
                    Inventory inv = pa.game.playerInventory.GetValue(receiver);

                    if (mt == MessageType.LOOT_PICKUP)
                        pa.OnLootPickup(inv, p);
                    else if (mt == MessageType.LOOT_CONSUMED)
                        pa.OnLootConsumed(inv, p);
                    else
                        throw new Exception("PlayerActorProcessor: Unsupported message " + mt.ToString());
                });
            }

        }
    }*/
    
    /*class PlayerActor
    {
        internal Game game;

        AssignmentInfo gameInfo;

        public Action<World, Player, MoveType> onMoveHook = (w, pl, mv) => { };

        public PlayerActor(Game game_, AssignmentInfo gameInfo_)
        {
            game = game_;
            gameInfo = gameInfo_;
        }

        public void OnMove(World w, Player p, Point newPos)
        {
            w.Move(p.id, newPos, MoveValidity.VALID);
            onMoveHook(w, p, MoveType.MOVE);
        }
        public void OnTeleport(World w, Player p, Point newPos)
        {
            w.Move(p.id, newPos, MoveValidity.TELEPORT);
            onMoveHook(w, p, MoveType.MOVE);
        }
        public void OnLootPickup(Inventory inv, Player p)
        {
            inv.teleport++;

            if (gameInfo.IsMyRole(p.id))
            {
                Log.LogWriteLine("{0} pick up teleport, now has {1}", p.FullName, inv.teleport);
            }
        }
        public void OnLootConsumed(Inventory inv, Player p)
        {
            MyAssert.Assert(inv.teleport > 0);
            inv.teleport--;

            if (gameInfo.IsMyRole(p.id))
            {
                Log.LogWriteLine("{0} consumed teleport, now has {1}", p.FullName, inv.teleport);
            }
        }

        public void OnRemovePlayer(World w, Player p)
        {
            w.RemovePlayer(p.id);

            Log.LogWriteLine("Player {0} removed from world {1}", p.FullName, w.worldPosition.ToString());
            onMoveHook(w, p, MoveType.LEAVE);
        }
        public void OnAddPlayer(World w, Player p, Point newPos)
        {
            MyAssert.Assert(!game.playerWorld.GetValue(p.id).Equals(w.worldPosition));
            game.playerWorld[p.id] = w.worldPosition;
            w.AddPlayer(p.id, newPos);

            Log.LogWriteLine("Player {0} added to world {1} at {2}", p.FullName, w.worldPosition, newPos);
            onMoveHook(w, p, MoveType.JOIN);
        }
    }*/



    class Aggregator
    {
        public ActionSyncronizer sync = new ActionSyncronizer();
        public GlobalHost host;

        public Client myClient;
        public Server myServer = null;

        public Dictionary<Point, WorldValidator> worldValidators = new Dictionary<Point, WorldValidator>();
        public Dictionary<Guid, PlayerValidator> playerValidators = new Dictionary<Guid, PlayerValidator>();
        public Dictionary<Guid, PlayerAgent> playerAgents = new Dictionary<Guid, PlayerAgent>();

        public Aggregator()
        {
            lock (sync.syncLock)
            {
                host = new GlobalHost(sync.GetAsDelegate());
                myClient = new Client(sync.GetAsDelegate(), host, this);
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
            myServer = new Server(sync.GetAsDelegate(), host);
            myClient.OnServerAddress(myServer.Address);
        }

        public void AddWorldValidator(WorldInfo info, WorldInitializer init)
        {
            MyAssert.Assert(!worldValidators.ContainsKey(info.worldPos));
            worldValidators.Add(info.worldPos, new WorldValidator(info, init, sync.GetAsDelegate(), host, myClient.gameInfo));
        }
        public void AddPlayerValidator(PlayerInfo info)
        {
            MyAssert.Assert(!playerValidators.ContainsKey(info.id));
            playerValidators.Add(info.id, new PlayerValidator(info, sync.GetAsDelegate(), host, myClient.gameInfo));
        }
        public void AddPlayerAgent(PlayerInfo info)
        {
            MyAssert.Assert(!playerAgents.ContainsKey(info.id));
            PlayerAgent pa = new PlayerAgent(info, sync.GetAsDelegate(), host, myClient.gameInfo);
            playerAgents.Add(info.id, pa);

            ThreadManager.NewThread(() =>
                {
                    while (true)
                    {
                        int sleepTime;
                        
                        lock(sync.syncLock)    
                            sleepTime = Program.PlayerAi(myClient, pa);

                        Thread.Sleep(sleepTime);
                    }
                }, () => {}, "Ai for " + info.GetShortInfo());
        }

        public void SpawnAll()
        {
            foreach (Guid id in myClient.myPlayerAgents)
                playerAgents.GetValue(id).Spawn(new Point(0, 0));
        }
    }
}
