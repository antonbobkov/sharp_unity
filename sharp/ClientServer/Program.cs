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

        public Aggregate()
        {
            sync = new ActionSyncronizer();
            peers = new NodeCollection(sync.GetAsDelegate(), ProcessMessage, OnNewConnection);
        }

        public void Connect(List<string> param, bool mesh = false)
        {
            IPAddress ip = NetTools.GetMyIP();
            int port = NodeCollection.nStartPort;

            foreach (var s in param)
            {
                IPAddress parseIp;
                if (IPAddress.TryParse(s, out parseIp))
                {
                    ip = parseIp;
                    continue;
                }

                int parsePort;
                if (int.TryParse(s, out parsePort))
                {
                    port = parsePort;
                    continue;
                }
            }

            IPEndPoint ep = new IPEndPoint(ip, port);
            Log.LogWriteLine("Connecting to {0} {1}", ep.Address, ep.Port);

            Node n = peers.TryConnectAsync(ep);
            
            if (n == null)
            {
                Log.LogWriteLine("Already connected/connecting");
                return;
            }

            if (mesh)
                n.SendMessage(MessageType.TABLE_REQUEST);
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
            Console.WriteLine("Game generated, controlled by {0}", game.worldValidator == myRole.validator ? "me!" : game.worldValidator.ToString());
            game.ConsoleOut();
        }
        void OnValidateMove(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(game.worldValidator == myRole.validator);
            Debug.Assert(roles.players.ContainsKey(mv.id));
            Debug.Assert(roles.players[mv.id] == n);

            MoveValidity v = game.CheckValidMove(mv);
            if (v != MoveValidity.VALID)
            {
                Console.WriteLine("Validator: Invalid move {0} from {1} to {2} by {3}", v, game.players[mv.id].pos, mv.pos, mv.id);
                return;
            }

            Broadcast(MessageType.MOVE, mv);
        }
        void OnMove(Node n, PlayerMoveInfo mv)
        {
            Debug.Assert(roles.players.ContainsKey(mv.id));
            Debug.Assert(game.CheckValidMove(mv) == MoveValidity.VALID);
            Debug.Assert(roles.validators[game.worldValidator] == n);
            Debug.Assert(game.CheckValidMove(mv) == MoveValidity.VALID);

            game.Move(mv);
        }

        public void Broadcast(MessageType mt){Broadcast<object>(mt, null);}
        public void Broadcast<T>(MessageType mt, T obj)
        {
            foreach (var n in peers.GetAllNodes())
                n.SendMessage(mt, obj);
        }
        public void GenerateGame()
        {
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
        
        static void PlayerRandomMove(Aggregate a)
        {
            Game g = a.game;
            Player p = g.players[a.myRole.player];

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
            all.sync.Add(() => all.Connect(new List<string>(), true));

            try
            {
                var f = new System.IO.StreamReader("ips");
                string line;
                while ((line = f.ReadLine()) != null)
                    all.sync.Add(() => all.Connect(new List<string>(line.Split(' ')), true));
            }
            catch (Exception) { }
        }
        
        static void Main(string[] args)
        {
            Aggregate all = new Aggregate();
            all.myRole.player = Guid.NewGuid();
            all.myRole.validator = Guid.NewGuid();
            
            Console.WriteLine();
            Console.WriteLine("Player {0}", all.myRole.player);
            Console.WriteLine("Validator {0}", all.myRole.validator);
            Console.WriteLine();

            //mesh connect
            Program.MeshConnect(all);

            InputProcessor inputProc = new InputProcessor(all.sync.GetAsDelegate());

            inputProc.commands.Add("connect", (param) => all.Connect(param, false));
            inputProc.commands.Add("mconnect", (param) => all.Connect(param, true));
            
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
                new Thread(() => RepeatedAction(all.sync.GetAsDelegate(), () => PlayerRandomMove(all), 500)).Start();
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
