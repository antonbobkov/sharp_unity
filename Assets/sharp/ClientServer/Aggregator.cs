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

        public Role myRole = new Role();
        public NodeRoles roles = new NodeRoles();

        public Game game = null;
        public Game validatorGame = null;

        public Dictionary<Guid, Inventory> validatedInventories = new Dictionary<Guid, Inventory>();

        public Action<PlayerMoveInfo> onMoveHook = (mv => { });

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
        void OnValidateMove(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(myRole.validator.Contains(game.worldValidator));
            Debug.Assert(roles.players.ContainsKey(mv.id));
            Debug.Assert(roles.players[mv.id] == n);

            MoveValidity v = validatorGame.CheckValidMove(mv);
            //Log.LogWriteLine("Validator: Move {0} from {1} to {2} by {3}", v, game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("Validator: Invalid move {0} from {1} to {2} by {3}", v, game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
                return;
            }

            Player p = validatorGame.players[mv.id];
            Tile t = validatorGame.world[mv.pos.x, mv.pos.y];
            if (t.loot)
                roles.validators[p.validator].SendMessage(MessageType.LOOT_PICKUP_BROADCAST, mv.id);

            validatorGame.Move(mv);

            Broadcast(MessageType.MOVE, mv);
        }
        void OnMove(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(roles.players.ContainsKey(mv.id));
            Debug.Assert(game.CheckValidMove(mv) == MoveValidity.VALID);
            Debug.Assert(roles.validators[game.worldValidator] == n);

            //if (game.CheckValidMove(mv) != MoveValidity.VALID)
            {
                //Log.LogWriteLine("Player: Move {0} from {1} to {2} by {3}", game.CheckValidMove(mv), game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
            }


            game.Move(mv);

            onMoveHook(mv);
        }
        void OnLoot(Node n, Guid id)
        {
            Debug.Assert(game.players.ContainsKey(id));

            Player p = game.players[id];

            Debug.Assert(roles.validators[p.validator] == n);

            p.inv.teleport++;

            if (myRole.player.Contains(id))
            {
                Log.LogWriteLine("{0} pick up teleport, now has {1}", p.FullName, p.inv.teleport);
            }
        }
        void OnLootBroadcast(Node n, Guid id)
        {
            Debug.Assert(game.players.ContainsKey(id));

            Player p = game.players[id];

            Debug.Assert(myRole.validator.Contains(p.validator));

            // Log.LogWriteLine("OnLootBroadcast: broadcasting for {0}", p.FullName);

            Debug.Assert(validatedInventories.ContainsKey(p.id));

            validatedInventories[p.id].teleport++;
            Log.LogWriteLine("{0} pick up teleport, now has {1} (validator)", p.FullName, validatedInventories[p.id].teleport);

            Broadcast(MessageType.LOOT_PICKUP, id);
        }
        void OnValidateTeleport(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(myRole.validator.Contains(game.worldValidator));
            Debug.Assert(roles.players.ContainsKey(mv.id));
            Debug.Assert(roles.players[mv.id] == n);
            Player p = validatorGame.players[mv.id];

            MoveValidity v = validatorGame.CheckValidMove(mv);
            //Log.LogWriteLine("Validator: Move {0} from {1} to {2} by {3}", v, game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
            if (false && v != MoveValidity.TELEPORT)
            {
                Log.LogWriteLine("Validator: Invalid (step 1) teleport {0} from {1} to {2} by {3}", v, p.pos, mv.pos, p.FullName);
                return;
            }

            Log.LogWriteLine("Validator: Freezing request for teleport from {1} to {2} by {3}", v, p.pos, mv.pos, p.FullName);
            roles.validators[p.validator].SendMessage(MessageType.FREEZE_ITEM, mv);
        }
        void OnFreezeItem(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(game.players.ContainsKey(mv.id));
            Player p = game.players[mv.id];
            Debug.Assert(myRole.validator.Contains(p.validator));

            Inventory inv = validatedInventories[mv.id];

            if (inv.teleport > 0)
            {
                inv.teleport--;
                inv.frozenTeleport++;

                roles.validators[game.worldValidator].SendMessage(MessageType.FREEZING_SUCCESS, mv);

                Log.LogWriteLine("{0} freezes one teleport (validator)", p.FullName);
            }
            else
                Log.LogWriteLine("{0} freeze failed (validator)", p.FullName);
        }
        void OnFreezeSuccess(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(myRole.validator.Contains(game.worldValidator));
            Debug.Assert(roles.players.ContainsKey(mv.id));
            Player p = validatorGame.players[mv.id];
            Debug.Assert(roles.validators[p.validator] == n);

            Log.LogWriteLine("Validator: Freeze sucessful. Trying to teleport from {0} to {1} by {2}.", p.pos, mv.pos, p.FullName);

            MoveValidity v = validatorGame.CheckValidMove(mv);
            //Log.LogWriteLine("Validator: Move {0} from {1} to {2} by {3}", v, game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
            if (v != MoveValidity.TELEPORT)
            {
                Log.LogWriteLine("Validator: Invalid (step 2) teleport {0} from {1} to {2} by {3}", v, p.pos, mv.pos, p.FullName);
                roles.validators[p.validator].SendMessage(MessageType.UNFREEZE_ITEM, mv);
                return;
            }

            validatorGame.Move(mv);
            roles.validators[p.validator].SendMessage(MessageType.CONSUME_FROZEN_ITEM, mv);

            Broadcast(MessageType.TELEPORT, mv);
        }
        void OnUnfreeze(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(game.players.ContainsKey(mv.id));
            Player p = game.players[mv.id];
            Debug.Assert(myRole.validator.Contains(p.validator));

            Inventory inv = validatedInventories[mv.id];

            Debug.Assert(inv.frozenTeleport > 0);
            inv.teleport++;
            inv.frozenTeleport--;

            Log.LogWriteLine("{0} unfreezes one teleport (validator)", p.FullName);
        }
        void OnConsumeFrozen(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(game.players.ContainsKey(mv.id));
            Player p = game.players[mv.id];
            Debug.Assert(myRole.validator.Contains(p.validator));

            Inventory inv = validatedInventories[mv.id];

            Debug.Assert(inv.frozenTeleport > 0);
            inv.frozenTeleport--;

            Log.LogWriteLine("{0} consume teleport, now has {1} (validator)", p.FullName, inv.teleport);
            Broadcast(MessageType.LOOT_CONSUMED, mv);
        }
        void OnTeleport(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(roles.players.ContainsKey(mv.id));
            Debug.Assert(game.CheckValidMove(mv) == MoveValidity.TELEPORT);
            Debug.Assert(roles.validators[game.worldValidator] == n);

            //if (game.CheckValidMove(mv) != MoveValidity.VALID)
            {
                //Log.LogWriteLine("Player: Move {0} from {1} to {2} by {3}", game.CheckValidMove(mv), game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
            }


            game.Move(mv);

            onMoveHook(mv);
        }
        void OnLootConsumed(Node n, PlayerMoveInfo mv)
        {
            Guid id = mv.id;
            Debug.Assert(game.players.ContainsKey(id));

            Player p = game.players[id];

            Debug.Assert(roles.validators[p.validator] == n);

            Debug.Assert(p.inv.teleport > 0);
            p.inv.teleport--;

            if (myRole.player.Contains(id))
            {
                Log.LogWriteLine("{0} consumed teleport, now has {1}", p.FullName, p.inv.teleport);
            }
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

    class WorldValidatorProcessor
    {
        Aggregate all;
        public Dictionary<Guid, WorldValidator> validators;

        public readonly HashSet<MessageType> myMessages = new HashSet<MessageType>()
        {
            MessageType.VALIDATE_MOVE,
            MessageType.VALIDATE_TELEPORT,
            MessageType.FREEZING_SUCCESS
        };

        public void ProcessMessage(Node n, MessageType mt, Stream stm)
        {
            Guid idThem = Serializer.Deserialize<Guid>(stm);
            Guid idMe = Serializer.Deserialize<Guid>(stm);
            PlayerMoveInfo mv = Serializer.Deserialize<PlayerMoveInfo>(stm);

            all.sync.Add(() =>
            {
                //Debug.Assert(all.nodemap[idThem] == n);

                if (mt == MessageType.VALIDATE_MOVE || mt == MessageType.VALIDATE_TELEPORT)
                    Debug.Assert(idThem == mv.id);

                Debug.Assert(validators.ContainsKey(idMe));
                WorldValidator wv = validators[idMe];
                Debug.Assert(wv.myId == idMe);

                Debug.Assert(wv.validatorGame.players.ContainsKey(mv.id));
                Player p = wv.validatorGame.players[mv.id];

                if (mt == MessageType.VALIDATE_MOVE)
                    wv.OnValidateMove(p, mv);
                else if (mt == MessageType.VALIDATE_TELEPORT)
                    wv.OnValidateTeleport(p, mv);
                else if (mt == MessageType.FREEZING_SUCCESS)
                    wv.OnFreezeSuccess(p, mv);
                else
                    throw new Exception("WorldValidatorProcessor: Unsupported message " + mt.ToString());
            });
        }
    }

    class WorldValidator
    {
        internal Game validatorGame;
        internal Guid myId;
        Aggregate all;

        internal void OnValidateMove(Player p, PlayerMoveInfo mv)
        {
            Point newPos = mv.pos;
            
            MoveValidity v = validatorGame.CheckValidMove(mv);
            if (v != MoveValidity.VALID)
            {
                Log.LogWriteLine("Validator: Invalid move {0} from {1} to {2} by {3}", v, p.pos, newPos, p.FullName);
                return;
            }

            Tile t = validatorGame.world[newPos.x, newPos.y];
            if (t.loot)
                all.roles.validators[p.validator].SendMessage(MessageType.LOOT_PICKUP_BROADCAST, myId, p.id);

            validatorGame.Move(mv);

            all.Broadcast(MessageType.MOVE, myId, mv);
        }
        internal void OnValidateTeleport(Player p, PlayerMoveInfo mv)
        {
            Point newPos = mv.pos;

            MoveValidity v = validatorGame.CheckValidMove(mv);
            //Log.LogWriteLine("Validator: Move {0} from {1} to {2} by {3}", v, game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
            if (false && v != MoveValidity.TELEPORT)
            {
                Log.LogWriteLine("Validator: Invalid (step 1) teleport {0} from {1} to {2} by {3}", v, p.pos, mv.pos, p.FullName);
                return;
            }

            Log.LogWriteLine("Validator: Freezing request for teleport from {1} to {2} by {3}", v, p.pos, mv.pos, p.FullName);

            all.roles.validators[p.validator].SendMessage(MessageType.FREEZE_ITEM, myId, p.validator, mv);     
        }
        internal void OnFreezeSuccess(Player p, PlayerMoveInfo mv)
        {
            Log.LogWriteLine("Validator: Freeze sucessful. Trying to teleport from {0} to {1} by {2}.", p.pos, mv.pos, p.FullName);

            MoveValidity v = validatorGame.CheckValidMove(mv);
            if (v != MoveValidity.TELEPORT)
            {
                Log.LogWriteLine("Validator: Invalid (step 2) teleport {0} from {1} to {2} by {3}", v, p.pos, mv.pos, p.FullName);
                all.roles.validators[p.validator].SendMessage(MessageType.UNFREEZE_ITEM, myId, p.validator);
                return;
            }

            validatorGame.Move(mv);

            all.roles.validators[p.validator].SendMessage(MessageType.CONSUME_FROZEN_ITEM, myId, p.validator);
            all.Broadcast(MessageType.TELEPORT, myId, mv);
        }

    }

    class PlayerValidatorProcessor
    {
        Aggregate all;
        public Dictionary<Guid, PlayerValidator> validators;

        public readonly HashSet<MessageType> myMessages = new HashSet<MessageType>()
        {
            MessageType.VALIDATE_MOVE,
            MessageType.VALIDATE_TELEPORT,
            MessageType.FREEZING_SUCCESS
        };

        public void ProcessMessage(Node n, MessageType mt, Stream stm)
        {
            Guid idThem = Serializer.Deserialize<Guid>(stm);
            Guid idMe = Serializer.Deserialize<Guid>(stm);

            PlayerMoveInfo mv = null;
            if(mt == MessageType.FREEZE_ITEM)
                mv = Serializer.Deserialize<PlayerMoveInfo>(stm);

            all.sync.Add(() =>
            {
                //Debug.Assert(all.nodemap[idThem] == n);
                Debug.Assert(all.game.worldValidator == idThem);

                Debug.Assert(validators.ContainsKey(idMe));
                PlayerValidator pv = validators[idMe];
                Debug.Assert(pv.myId == idMe);

                if (mt == MessageType.LOOT_PICKUP_BROADCAST)
                    pv.OnLootBroadcast();
                else if (mt == MessageType.FREEZE_ITEM)
                    pv.OnFreezeItem(mv);
                else if (mt == MessageType.UNFREEZE_ITEM)
                    pv.OnUnfreeze();
                else if (mt == MessageType.CONSUME_FROZEN_ITEM)
                    pv.OnConsumeFrozen();
                else
                    throw new Exception("PlayerValidatorProcessor: Unsupported message " + mt.ToString());
            });
        }
    }

    class PlayerValidator
    {
        internal Inventory validatorInventory;
        Player p;
        internal Guid myId;
        Aggregate all;

        internal void OnLootBroadcast()
        {
            validatorInventory.teleport++;
            Log.LogWriteLine("{0} pick up teleport, now has {1} (validator)", p.FullName, validatorInventory.teleport);

            all.Broadcast(MessageType.LOOT_PICKUP, myId, p.id);
        }
        internal void OnFreezeItem(PlayerMoveInfo mv)
        {
            if (validatorInventory.teleport > 0)
            {
                validatorInventory.teleport--;
                validatorInventory.frozenTeleport++;

                all.roles.validators[all.game.worldValidator].SendMessage(MessageType.FREEZING_SUCCESS, myId, all.game.worldValidator, mv);

                Log.LogWriteLine("{0} freezes one teleport (validator)", p.FullName);
            }
            else
                Log.LogWriteLine("{0} freeze failed (validator)", p.FullName);
        }
        internal void OnUnfreeze()
        {
            Debug.Assert(validatorInventory.frozenTeleport > 0);
            validatorInventory.teleport++;
            validatorInventory.frozenTeleport--;

            Log.LogWriteLine("{0} unfreezes one teleport (validator)", p.FullName);
        }
        internal void OnConsumeFrozen()
        {
            Debug.Assert(validatorInventory.frozenTeleport > 0);
            validatorInventory.frozenTeleport--;

            Log.LogWriteLine("{0} consume teleport, now has {1} (validator)", p.FullName, validatorInventory.teleport);
            all.Broadcast(MessageType.LOOT_CONSUMED, myId, p.id);
        }
    }

    class PlayerActorProcessor
    {
        Aggregate all;
        PlayerActor pa;

        public readonly HashSet<MessageType> myMessages = new HashSet<MessageType>()
        {
            MessageType.VALIDATE_MOVE,
            MessageType.VALIDATE_TELEPORT,
            MessageType.FREEZING_SUCCESS
        };

        public void ProcessMessage(Node n, MessageType mt, Stream stm)
        {
            Guid idThem = Serializer.Deserialize<Guid>(stm);

            Action verify = () =>
            {
                //Debug.Assert(all.nodemap[idThem] == n);
                Debug.Assert(all.game.worldValidator == idThem);
            };
            
            if (mt == MessageType.MOVE || mt == MessageType.TELEPORT)
            {
                PlayerMoveInfo mv = Serializer.Deserialize<PlayerMoveInfo>(stm);

                all.sync.Add(() =>
                {
                    if (mt == MessageType.MOVE)
                        pa.OnMove(mv);
                    else if (mt == MessageType.TELEPORT)
                        pa.OnTeleport(mv);
                    else
                        throw new Exception("PlayerActorProcessor: Unsupported message " + mt.ToString());
                });
            }
            else
            {
                Guid id = Serializer.Deserialize<Guid>(stm);
                Debug.Assert(all.game.players.ContainsKey(id));
                Player p = all.game.players[id];
                
                all.sync.Add(() =>
                {
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
        Aggregate all;

        internal void OnMove(PlayerMoveInfo mv)
        {
            //if (game.CheckValidMove(mv) != MoveValidity.VALID)
            {
                //Log.LogWriteLine("Player: Move {0} from {1} to {2} by {3}", game.CheckValidMove(mv), game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
            }


            all.game.Move(mv);

            all.onMoveHook(mv);
        }
        internal void OnTeleport(PlayerMoveInfo mv)
        {
            //if (game.CheckValidMove(mv) != MoveValidity.VALID)
            {
                //Log.LogWriteLine("Player: Move {0} from {1} to {2} by {3}", game.CheckValidMove(mv), game.players[mv.id].pos, mv.pos, game.players[mv.id].FullName);
            }


            all.game.Move(mv);

            all.onMoveHook(mv);
        }
        internal void OnLootPickup(Player p)
        {
            p.inv.teleport++;

            if (all.myRole.player.Contains(p.id))
            {
                Log.LogWriteLine("{0} pick up teleport, now has {1}", p.FullName, p.inv.teleport);
            }
        }
        internal void OnLootConsumed(Player p)
        {
            Debug.Assert(p.inv.teleport > 0);
            p.inv.teleport--;

            if (all.myRole.player.Contains(p.id))
            {
                Log.LogWriteLine("{0} consumed teleport, now has {1}", p.FullName, p.inv.teleport);
            }
        }
    }
}
