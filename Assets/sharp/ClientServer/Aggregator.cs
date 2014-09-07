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
    class Aggregate
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

            gameAssignments.validators.Add(game.worldValidator, game.worldValidator);
            gameAssignments.roles.Add(game.worldValidator, NodeRole.WORLD_VALIDATOR);

            manager.playerMessageReciever.pa = new PlayerActor(game, gameAssignments);

            Log.LogWriteLine("Game generated, controlled by {0}",
                gameAssignments.IsMyRole(game.worldValidator) ?
                    "me!" : gameAssignments.NodeById(game.worldValidator).Address.ToString());

            Role myRole = gameAssignments.GetMyRole();
            
            if (myRole.player.Any())
            {
                StringBuilder sb = new StringBuilder("My player #'s: ");
                sb.Append(String.Join(" ", (from id in myRole.player select game.players[id].sName).ToArray()));
                Log.LogWriteLine("{0}", sb.ToString());
            }

            var validatedPlayers = (from pl in game.players.Values
                                    where gameAssignments.IsMyRole(pl.validator)
                                    select pl).ToArray();

            if (validatedPlayers.Any())
            {
                StringBuilder sb = new StringBuilder("validating for: ");
                sb.Append(String.Join(" ", (from pl in validatedPlayers select pl.sName).ToArray()));
                Log.LogWriteLine("{0}", sb.ToString());
            }


            Action<MessageType, object[]> broadcaster = (mt, arr) => Broadcast(mt, arr);            
            foreach (Player p in validatedPlayers)
                manager.playerValidatorMessageReciever.validators.Add(p.id,
                    new PlayerValidator(p.id, gameAssignments, broadcaster, p.FullName));

            if(gameAssignments.IsMyRole(game.worldValidator))
                manager.playerWorldVerifierMessageReciever.validators.Add(game.worldValidator,
                    new WorldValidator(game.worldValidator, gameAssignments, broadcaster, init));

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
        public void Move(Player p, Point newPos)
        {
            gameAssignments.NodeById(game.worldValidator).SendMessage(MessageType.VALIDATE_MOVE, p.id, game.worldValidator, newPos);
        }

        public void SetMoveHook(Action<Player, Point> hook) { manager.playerMessageReciever.pa.onMoveHook = hook; }
    }

    class Validator
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
    }

    class WorldValidatorProcessor : MessageReceiver
    {
        public Dictionary<Guid, WorldValidator> validators = new Dictionary<Guid,WorldValidator>();

        public override void ProcessMessage(MessageType mt, Guid sender, Guid receiver, Stream stm, Action<Action> syncronizer)
        {
            Point newPos = Serializer.Deserialize<Point>(stm);

            syncronizer.Invoke(() =>
            {
                WorldValidator wv = validators.GetValue(receiver);
                Player p = wv.validatorGame.players.GetValue(sender);

                if (mt == MessageType.VALIDATE_MOVE)
                    wv.OnValidateMove(p, newPos);
                else if (mt == MessageType.VALIDATE_TELEPORT)
                    wv.OnValidateTeleport(p, newPos);
                else if (mt == MessageType.FREEZING_SUCCESS)
                    wv.OnFreezeSuccess(p, newPos);
                else
                    throw new Exception("WorldValidatorProcessor: Unsupported message " + mt.ToString());
            });
        }
    }
    
    class WorldValidator : Validator
    {
        internal Game validatorGame;

        public WorldValidator(Guid myId_, AssignmentInfo gameInfo_, Action<MessageType, object[]> broadcaster_,
            GameInitializer init)
            : base(myId_, gameInfo_, broadcaster_)
        {
            validatorGame = new Game(init, gameInfo.GetAllRoles());
        }

        internal void OnValidateMove(Player p, Point newPos)
        {
            MoveValidity v = validatorGame.CheckValidMove(p, newPos);
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("Validator: Invalid move {0} from {1} to {2} by {3}", v, p.pos, newPos, p.FullName);
                return;
            }

            Tile t = validatorGame.world[newPos.x, newPos.y];
            if (t.loot)
                gameInfo.NodeById(p.validator).SendMessage(MessageType.LOOT_PICKUP_BROADCAST, myId, p.id);

            validatorGame.Move(p, newPos, MoveValidity.VALID);

            Broadcast(MessageType.MOVE, myId, p.id, newPos);
        }
        internal void OnValidateTeleport(Player p, Point newPos)
        {
            MoveValidity v = validatorGame.CheckValidMove(p, newPos);
            if (false && v != MoveValidity.TELEPORT)
            {
                Log.LogWriteLine("Validator: Invalid (step 1) teleport {0} from {1} to {2} by {3}", v, p.pos, newPos, p.FullName);
                return;
            }

            Log.LogWriteLine("Validator: Freezing request for teleport from {1} to {2} by {3}", v, p.pos, newPos, p.FullName);

            gameInfo.NodeById(p.validator).SendMessage(MessageType.FREEZE_ITEM, myId, p.id, newPos);
        }
        internal void OnFreezeSuccess(Player p, Point newPos)
        {
            Log.LogWriteLine("Validator: Freeze sucessful. Trying to teleport from {0} to {1} by {2}.", p.pos, newPos, p.FullName);

            MoveValidity v = validatorGame.CheckValidMove(p, newPos);
            if (v != MoveValidity.TELEPORT)
            {
                Log.LogWriteLine("Validator: Invalid (step 2) teleport {0} from {1} to {2} by {3}", v, p.pos, newPos, p.FullName);
                gameInfo.NodeById(p.validator).SendMessage(MessageType.UNFREEZE_ITEM, myId, p.id);
                return;
            }

            validatorGame.Move(p, newPos, MoveValidity.TELEPORT);

            gameInfo.NodeById(p.validator).SendMessage(MessageType.CONSUME_FROZEN_ITEM, myId, p.id);
            Broadcast(MessageType.TELEPORT, myId, p.id, newPos);
        }
    }

    class PlayerValidatorProcessor : MessageReceiver
    {
        public Dictionary<Guid, PlayerValidator> validators = new Dictionary<Guid,PlayerValidator>();

        public override void ProcessMessage(MessageType mt, Guid sender, Guid receiver, Stream stm, Action<Action> syncronizer)
        {
            if (mt == MessageType.FREEZE_ITEM)
            {
                Point newPos = Serializer.Deserialize<Point>(stm);
                syncronizer.Invoke(() => validators.GetValue(receiver).OnFreezeItem(sender, newPos));
            }
            else
            {
                syncronizer.Invoke(() =>
                    {
                        PlayerValidator pv = validators.GetValue(receiver);
                        if (mt == MessageType.LOOT_PICKUP_BROADCAST)
                            pv.OnLootBroadcast();
                        else if (mt == MessageType.UNFREEZE_ITEM)
                            pv.OnUnfreeze();
                        else if (mt == MessageType.CONSUME_FROZEN_ITEM)
                            pv.OnConsumeFrozen();
                        else
                            throw new Exception("PlayerValidatorProcessor: Unsupported message " + mt.ToString());
                    });
            }
        }
    }

    class PlayerValidator : Validator
    {
        Inventory validatorInventory = new Inventory();
        string name;

        public PlayerValidator(Guid myId_, AssignmentInfo gameInfo_, Action<MessageType, object[]> broadcaster_,
            string name_) : base(myId_, gameInfo_, broadcaster_)
        {
            name = name_;
        }

        internal void OnLootBroadcast()
        {
            validatorInventory.teleport++;
            Log.LogWriteLine("{0} pick up teleport, now has {1} (validator)", name, validatorInventory.teleport);

            Broadcast(MessageType.LOOT_PICKUP, myId, myId);
        }
        internal void OnFreezeItem(Guid worldId, Point newPos)
        {
            if (validatorInventory.teleport > 0)
            {
                validatorInventory.teleport--;
                validatorInventory.frozenTeleport++;

                gameInfo.NodeById(worldId).SendMessage(MessageType.FREEZING_SUCCESS, myId, worldId, newPos);

                Log.LogWriteLine("{0} freezes one teleport (validator)", name);
            }
            else
                Log.LogWriteLine("{0} freeze failed (validator)", name);
        }
        internal void OnUnfreeze()
        {
            MyAssert.Assert(validatorInventory.frozenTeleport > 0);
            validatorInventory.teleport++;
            validatorInventory.frozenTeleport--;

            Log.LogWriteLine("{0} unfreezes one teleport (validator)", name);
        }
        internal void OnConsumeFrozen()
        {
            MyAssert.Assert(validatorInventory.frozenTeleport > 0);
            validatorInventory.frozenTeleport--;

            Log.LogWriteLine("{0} consume teleport, now has {1} (validator)", name, validatorInventory.teleport);
            Broadcast(MessageType.LOOT_CONSUMED, myId, myId);
        }
    }

    class PlayerActorProcessor : MessageReceiver
    {
        public PlayerActor pa = null;

        public override void ProcessMessage(MessageType mt, Guid sender, Guid receiver, Stream stm, Action<Action> syncronizer)
        {
            
            if (mt == MessageType.MOVE || mt == MessageType.TELEPORT)
            {
                Point newPos = Serializer.Deserialize<Point>(stm);

                syncronizer.Invoke(() =>
                {
                    Player p = pa.game.players.GetValue(receiver);

                    if (mt == MessageType.MOVE)
                        pa.OnMove(p, newPos);
                    else if (mt == MessageType.TELEPORT)
                        pa.OnTeleport(p, newPos);
                    else
                        throw new Exception("PlayerActorProcessor: Unsupported message " + mt.ToString());
                });
            }
            else
            {
                syncronizer.Invoke(() =>
                {
                    Player p = pa.game.players.GetValue(receiver);

                    if (mt == MessageType.LOOT_PICKUP)
                        pa.OnLootPickup(p);
                    else if (mt == MessageType.LOOT_CONSUMED)
                        pa.OnLootConsumed(p);
                    else
                        throw new Exception("PlayerActorProcessor: Unsupported message " + mt.ToString());
                });
            }

        }
    }
    
    class PlayerActor
    {
        internal Game game;

        AssignmentInfo gameInfo;

        public Action<Player, Point> onMoveHook = (pl, pos) => { };

        public PlayerActor(Game game_, AssignmentInfo gameInfo_)
        {
            game = game_;
            gameInfo = gameInfo_;
        }

        internal void OnMove(Player p, Point newPos)
        {
            game.Move(p, newPos, MoveValidity.VALID);
            onMoveHook(p, newPos);
        }
        internal void OnTeleport(Player p, Point newPos)
        {
            game.Move(p, newPos, MoveValidity.TELEPORT);
            onMoveHook(p, newPos);
        }
        internal void OnLootPickup(Player p)
        {
            p.inv.teleport++;

            if (gameInfo.IsMyRole(p.id))
            {
                Log.LogWriteLine("{0} pick up teleport, now has {1}", p.FullName, p.inv.teleport);
            }
        }
        internal void OnLootConsumed(Player p)
        {
            MyAssert.Assert(p.inv.teleport > 0);
            p.inv.teleport--;

            if (gameInfo.IsMyRole(p.id))
            {
                Log.LogWriteLine("{0} consumed teleport, now has {1}", p.FullName, p.inv.teleport);
            }
        }
    }
}
