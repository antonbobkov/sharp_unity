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

        public Dictionary<Guid, Inventory> validatedInventories = new Dictionary<Guid,Inventory>();

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
            if(!Connect(ep, mesh))
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

        public void Broadcast(MessageType mt){Broadcast<object>(mt, null);}
        public void Broadcast<T>(MessageType mt, T obj)
        {
            foreach (var n in peers.GetAllNodes())
                n.SendMessage(mt, obj);
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

    class Program
    {
        static Random rand = new Random();
        
        static void PlayerRandomMove(Aggregate a, Guid player)
        {
            Game g = a.game;
            if (g == null)
                return;
            Player p = g.players[player];

            Point[] moves = {
                                new Point(-1, 0),
                                new Point(1, 0),
                                new Point(0, 1),
                                new Point(0, -1),
                                new Point(-1, 1),
                                new Point(1, -1),
                                new Point(1, 1),
                                new Point(-1, -1)
                            };
            Point newPosition = p.pos + moves[rand.Next(0, moves.Length)];
            
            PlayerMoveInfo mv = new PlayerMoveInfo(p.id, newPosition);
            if (g.CheckValidMove(mv) == MoveValidity.VALID)
                if (!a.roles.validators[g.worldValidator].IsClosed)
                    a.Move(mv);
        }

        static void RepeatedAction(Action<Action> queue, Action a, int period)
        {
            while (true)
            {
                queue(a);
                Thread.Sleep(period);
            }
        }

        class InputProcessor
        {
            public Dictionary<string, Action<List<string>>> commands = new Dictionary<string, Action<List<string>>>();
            Action<Action> queueAction;

            public InputProcessor(Action<Action> queueAction_) { queueAction = queueAction_; } 

            public void Process(string input, List<string> param)
            {
                var a = from kv in commands
                        where kv.Key.StartsWith(input)
                        select kv.Key;
                int sz = a.Count();

                if(sz == 0)
                {
                    Console.WriteLine("unknown command");
                }
                else if (sz == 1)
                {
                    queueAction(() => commands[a.First()].Invoke(param));
                }
                else
                {
                    foreach (var s in a)
                        Console.WriteLine(s);
                }
            }
        }

        static public void MeshConnect(Aggregate all)
        {
            all.sync.Add(() =>
                {
                    IPEndPoint ep = Aggregate.ParseParamForIP(new List<string>());
                    if(all.Connect(ep, true))
                        Log.LogWriteLine("Connecting to {0} {1}", ep.Address, ep.Port);
                });

            try
            {
                var f = new System.IO.StreamReader(File.Open("ips", FileMode.Open));
                string line;
                while ((line = f.ReadLine()) != null)
                {
                    List<string> ls = new List<string>(line.Split(' '));
                    all.sync.Add(() =>
                    {
                        IPEndPoint ep = Aggregate.ParseParamForIP(ls);
                        if (all.Connect(ep, true))
                            Log.LogWriteLine("Connecting to {0} {1}", ep.Address, ep.Port);
                    });
                }
            }
            catch (Exception)
            {
                //Log.LogWriteLine("Error: {0}", e.Message);
            }
        }
        static public void Ai(Aggregate all)
        {
            foreach (var id in all.myRole.player)
            {
                Guid idCopy = id;
                new Thread(() => RepeatedAction(all.sync.GetAsDelegate(), () => PlayerRandomMove(all, idCopy), rand.Next(300, 700))).Start();
            }
        }
        
        static void Main(string[] args)
        {
            Aggregate all = new Aggregate();
            all.sync.Add(() =>
            {
                //if (all.peers.MyAddress.Port == NodeCollection.nStartPort)
                    all.myRole.validator.Add(Guid.NewGuid());
                //else
                {
                    for (int i = 0; i < 2; ++i)
                        all.myRole.player.Add(Guid.NewGuid());
                    Ai(all);
                }

                Console.WriteLine();
                Console.Write(all.myRole.PrintRoles());
                Console.WriteLine();

                //mesh connect
                Program.MeshConnect(all);
            });

            InputProcessor inputProc = new InputProcessor(all.sync.GetAsDelegate());

            inputProc.commands.Add("connect", (param) => all.ParamConnect(param, false));
            inputProc.commands.Add("mconnect", (param) => all.ParamConnect(param, true));
            
            inputProc.commands.Add("list", (param) =>
            {
                Console.WriteLine("Nodes:");
                foreach (var s in from n in all.peers.GetAllNodes() orderby n.Address.ToString() select n.Address.ToString())
                    Console.WriteLine("\t{0}", s);
                Console.WriteLine("Players:");
                foreach (var kv in from n in all.roles.players orderby n.Key.ToString() select n)
                    Console.WriteLine("\t{0}\t{1}", kv.Key, kv.Value.Address);
                Console.WriteLine("Validators:");
                foreach (var kv in from n in all.roles.validators orderby n.Key.ToString() select n)
                    Console.WriteLine("\t{0}\t{1}", kv.Key, kv.Value.Address);
            });

            inputProc.commands.Add("generate", (param) =>
            {
                all.GenerateGame();
            });

            inputProc.commands.Add("ai", (param) =>
            {
                Ai(all);
            });

            inputProc.commands.Add("draw", (param) =>
            {
                new Thread(() => RepeatedAction(all.sync.GetAsDelegate(), () => all.game.ConsoleOut(), 500)).Start();
            });


            while (true)
            {
                string sInput = Console.ReadLine();
                var param = new List<string>(sInput.Split(' '));
                if (!param.Any())
                    continue;
                string sCommand = param.First();
                param.RemoveRange(0, 1);
                inputProc.Process(sCommand, param);
            }


            /*
            while (true)
            {
                string sInput = Console.ReadLine();
                var param = new List<string>(sInput.Split(' '));
                string sCommand = param.FirstOrDefault();

                if ("message".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        if (param.Count < 3)
                            return;
                        string name = param[1];

                        param.RemoveRange(0, 2);

                        string message = String.Join(" ", param.ToArray());

                        Node n = myHost.dc.GetReadyNodes().FirstOrDefault(nd => nd.Name == name);
                        if (n == null)
                        {
                            Log.LogWriteLine("Invalid name");
                            return;
                        }

                        n.SendMessage(MessageType.MESSAGE, message);
                    });
                }
                else if ("name".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        if (param.Count != 2)
                            return;
                        string name = param[1];

                        Log.LogWriteLine("Name set to \"{0}\"", name);
                        myHost.dc.Name = name;

                        foreach (Node n in myHost.dc.GetReadyNodes())
                            n.SendMessage(MessageType.NAME, name);
                    });
                }
                else if ("list".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        StringBuilder sb = new StringBuilder();

                        foreach (Node n in myHost.dc.GetReadyNodes())
                        {
                            sb.Append(n.Address.ToString());
                            if (n.Name != n.Address.ToString())
                                sb.Append("\t" + n.Name);
                            sb.Append("\t" + n.Id.ToString("B"));
                            sb.Append("\n");
                        }

                        Console.Write(sb.ToString());
                    });
                }
                else if ("connect".StartsWith(sCommand) || "mconnect".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        string sIpAddr = NodeHost.GetMyIP().ToString();
                        string sPort = NodeHost.nStartPort.ToString();
                        bool askForTable = "mconnect".StartsWith(sCommand);

                        if (param.Count >= 2)
                            sIpAddr = param[1];

                        if (param.Count >= 3)
                            sPort = param[2];

                        IPEndPoint ep = new IPEndPoint(IPAddress.Parse(sIpAddr), Convert.ToUInt16(sPort));

                        Log.LogWriteLine("Connecting to {0}:{1}", ep.Address, ep.Port);

                        if (!myHost.dc.Sync_TryConnect(ep))
                            Log.LogWriteLine("Already connected/connecting");
                        else
                            Log.LogWriteLine("Connection started");

                        if (askForTable)
                            myHost.dc.Sync_AskForTable(ep);
                    });
                }
                else if ("draw".StartsWith(sCommand))
                {
                    sync.Add(() =>
                    {
                        if (myHost.dc.game == null)
                            return;

                        myHost.dc.game.ConsoleOut();
                    });
                }
                else if ("drawing".StartsWith(sCommand))
                {
                    new Thread(() => RepeatedAction(() =>
                        sync.Add(() =>
                        {
                            if (myHost.dc.game == null)
                                return;

                            Console.WriteLine();
                            myHost.dc.game.ConsoleOut();
                        })
                    , 500)).Start();
                }
                else if ("generate".StartsWith(sCommand))
                {
                    GameInitializer init = new GameInitializer(System.DateTime.Now.Millisecond, myHost.dc.GetReadyNodes().Count() + 1);
                    myHost.dc.Broadcast(MessageType.GENERATE_GAME, init);
                    myHost.dc.Sync_GenerateGame(init);
                }
                else if ("ai".StartsWith(sCommand))
                {
                    new Thread(() => RepeatedAction(() =>
                        sync.Add(() =>
                        {
                            PlayerRandomMove(myHost.dc.game, myHost.dc.game.players[myHost.dc.Id]);
                            myHost.dc.Sync_UpdateMyPosition();
                        })
                    , 1000)).Start();
                }
                else if ("exit".StartsWith(sCommand))
                {
                    sync.Add(null);
                    myHost.dc.TerminateThreads();
                    Console.ReadLine();
                    return;
                }
                else
                    Log.LogWriteLine("Unknown command");
            }
             * */
        }
    }
}
