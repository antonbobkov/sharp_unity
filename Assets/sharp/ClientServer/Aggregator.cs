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

        MessageTypeManager manager;
        AssignmentInfo gameAssignments;

        public Aggregate()
        {
            sync = new ActionSyncronizer();
            peers = new NodeCollection(sync.GetAsDelegate(), ProcessMessage, OnNewConnection);
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
                n.SendMessage(MessageType.TABLE_REQUEST);

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
            else if (mt == MessageType.VALIDATE_MOVE)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnValidateMove(n, mv));
            }
            else if (mt == MessageType.MOVE)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnMove(n, mv));
            }
            else if (mt == MessageType.LOOT_PICKUP)
            {
                var id = Serializer.Deserialize<Guid>(stm);
                sync.Add(() => OnLoot(n, id));
            }
            else if (mt == MessageType.LOOT_PICKUP_BROADCAST)
            {
                var id = Serializer.Deserialize<Guid>(stm);
                sync.Add(() => OnLootBroadcast(n, id));
            }
            else if (mt == MessageType.VALIDATE_TELEPORT)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnValidateTeleport(n, mv));
            }
            else if (mt == MessageType.FREEZE_ITEM)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnFreezeItem(n, mv));
            }
            else if (mt == MessageType.FREEZING_SUCCESS)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnFreezeSuccess(n, mv));
            }
            else if (mt == MessageType.UNFREEZE_ITEM)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnUnfreeze(n, mv));
            }
            else if (mt == MessageType.CONSUME_FROZEN_ITEM)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnConsumeFrozen(n, mv));
            }
            else if (mt == MessageType.TELEPORT)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnTeleport(n, mv));
            }
            else if (mt == MessageType.LOOT_CONSUMED)
            {
                var mv = Serializer.Deserialize<PlayerMoveInfo>(stm);
                sync.Add(() => OnLootConsumed(n, mv));
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
            n.SendMessage(MessageType.TABLE, a.ToArray());
        }
        void OnIpTable(IPEndPointSer[] table)
        {
            foreach (var ip in table)
                peers.TryConnectAsync(ip.Addr);
        }
        void OnRole(Node n, Role r)
        {
            roles.Add(r, n);
        }
        void OnNewConnection(Node n)
        {
            n.SendMessage(MessageType.ROLE, myRole);
        }
        void OnGenerate(GameInitializer init)
        {
            game = new Game(init, roles);

            Log.LogWriteLine("Game generated, controlled by {0}",
                myRole.validator.Contains(game.worldValidator) ?
                    "me!" : game.roles.validators[game.worldValidator].Address.ToString());

            if (myRole.player.Any())
            {
                StringBuilder sb = new StringBuilder("My player #'s: ");
                sb.Append(String.Join(" ", (from id in myRole.player select game.players[id].sName).ToArray()));
                Log.LogWriteLine("{0}", sb.ToString());
            }

            var validatedPlayers = (from pl in game.players.Values
                                    where myRole.validator.Contains(pl.validator)
                                    select pl).ToArray();

            if (validatedPlayers.Any())
            {
                StringBuilder sb = new StringBuilder("validating for: ");
                sb.Append(String.Join(" ", (from pl in validatedPlayers select pl.sName).ToArray()));
                Log.LogWriteLine("{0}", sb.ToString());
            }

            foreach (Player p in validatedPlayers)
            {
                validatedInventories[p.id] = new Inventory();
            }


            if (myRole.validator.Contains(game.worldValidator))
                validatorGame = new Game(init, roles);

            game.ConsoleOut();
        }
 
        public void Broadcast(MessageType mt, params Object[] messages)
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

            GameInitializer init = new GameInitializer(System.DateTime.Now.Millisecond, roles);
            Broadcast(MessageType.GENERATE, init);
        }
        public void Move(PlayerMoveInfo mv)
        {
            roles.validators[game.worldValidator].SendMessage(MessageType.VALIDATE_MOVE, mv);
        }
    }

    class Validator
    {
        protected Guid myId;
        protected AssignmentInfo gameInfo;

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
                gameInfo.NodeById(p.id).SendMessage(MessageType.LOOT_PICKUP_BROADCAST, myId, p.id);

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

            gameInfo.NodeById(p.id).SendMessage(MessageType.FREEZE_ITEM, myId, p.id, newPos);
        }
        internal void OnFreezeSuccess(Player p, Point newPos)
        {
            Log.LogWriteLine("Validator: Freeze sucessful. Trying to teleport from {0} to {1} by {2}.", p.pos, newPos, p.FullName);

            MoveValidity v = validatorGame.CheckValidMove(p, newPos);
            if (v != MoveValidity.TELEPORT)
            {
                Log.LogWriteLine("Validator: Invalid (step 2) teleport {0} from {1} to {2} by {3}", v, p.pos, newPos, p.FullName);
                gameInfo.NodeById(p.id).SendMessage(MessageType.UNFREEZE_ITEM, myId, p.validator);
                return;
            }

            validatorGame.Move(p, newPos, MoveValidity.TELEPORT);

            gameInfo.NodeById(p.id).SendMessage(MessageType.CONSUME_FROZEN_ITEM, myId, p.validator);
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
        Inventory validatorInventory;

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
            Debug.Assert(validatorInventory.frozenTeleport > 0);
            validatorInventory.teleport++;
            validatorInventory.frozenTeleport--;

            Log.LogWriteLine("{0} unfreezes one teleport (validator)", name);
        }
        internal void OnConsumeFrozen()
        {
            Debug.Assert(validatorInventory.frozenTeleport > 0);
            validatorInventory.frozenTeleport--;

            Log.LogWriteLine("{0} consume teleport, now has {1} (validator)", name, validatorInventory.teleport);
            Broadcast(MessageType.LOOT_CONSUMED, myId, myId);
        }
    }

    class PlayerActorProcessor : MessageReceiver
    {
        PlayerActor pa = null;

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

        Action<Player, Point> onMoveHook;

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
            Debug.Assert(p.inv.teleport > 0);
            p.inv.teleport--;

            if (gameInfo.IsMyRole(p.id))
            {
                Log.LogWriteLine("{0} consumed teleport, now has {1}", p.FullName, p.inv.teleport);
            }
        }
    }
}
