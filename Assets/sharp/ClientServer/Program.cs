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
    class PlayerMove
    {
        public ActionValidity mv;
        public Point newPos;
    }

    static class Program
    {
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
                    Console.WriteLine("unknown command");
                else if (sz == 1)
                    queueAction(() => commands[a.First()].Invoke(param));
                else
                    foreach (var s in a)
                        Console.WriteLine(s);
            }
        }
        static public void MeshConnect(Aggregator all, GameConfig cfg, IPAddress myIP)
        {
            foreach (var s in cfg.meshIPs)
            {
                IPEndPoint ep;
                
                if (s == "default")
                    ep = new IPEndPoint(NetTools.GetMyIP(), GlobalHost.nStartPort);
                else
                    ep = Aggregator.ParseParamForIP(new List<string>(s.Split(' ')), myIP);

                if (all.myClient.TryConnect(ep))
                    Log.Console("Mesh connect to {0} {1}", ep.Address, ep.Port);
            }
        }

        static public Guid NewAiPlayer(Aggregator all)
        {
            Guid newPlayer = Guid.NewGuid();

            all.myClient.NewMyPlayer(newPlayer);
            Ai.StartPlayerAiThread(all, newPlayer);

            return newPlayer;
        }

        static void Main(string[] args)
        {
            //Serializer.Test();

            GameConfig cfg_total = GameConfig.ReadConfig("game_config.xml");
            GameInstanceConifg cfg_local = cfg_total.clientConfig;

            IPAddress myIP = GameConfig.GetIP(cfg_total);

            Aggregator all = new Aggregator(myIP);

            bool myServer = cfg_total.startServer && all.host.MyAddress.Port == GlobalHost.nStartPort;

            if (myServer)
                cfg_local = cfg_total.serverConfig;

            all.myClient.onServerReadyHook = () =>
            {
                if (cfg_local.validate)
                    all.myClient.Validate();

                if (cfg_local.aiPlayers > 0)
                {
                    for (int i = 0; i < cfg_local.aiPlayers; ++i)
                        NewAiPlayer(all);
                    all.myClient.NewWorld(new Point(0, 0));
                }
            };

            MeshConnect(all, cfg_total, myIP);

            if (myServer)
                all.StartServer();

            all.sync.Start();

            InputProcessor inputProc = new InputProcessor(all.sync.GetAsDelegate());

            inputProc.commands.Add("connect", (param) => all.ParamConnect(param, myIP));

            inputProc.commands.Add("server", (param) =>
            {
                all.StartServer();
            });

            inputProc.commands.Add("player", (param) =>
            {
                NewAiPlayer(all);
            });
            
            inputProc.commands.Add("world", (param) =>
            {
                all.myClient.NewWorld(new Point(0, 0));
            });

            inputProc.commands.Add("validate", (param) =>
            {
                all.myClient.Validate();
            });

            inputProc.commands.Add("spawn", (param) =>
            {
                all.SpawnAll();
            });

            inputProc.commands.Add("draw", (param) =>
            {
                World w = all.myClient.knownWorlds.GetValue(new Point(0,0));
                ThreadManager.NewThread(() => RepeatedAction(all.sync.GetAsDelegate(),
                    () => WorldTools.ConsoleOut(w), 500),
                    () => { }, "console drawer");
            });

            inputProc.commands.Add("exit", (param) =>
            {
                all.host.Close();
                System.Threading.Thread.Sleep(100);
                ThreadManager.Terminate();
                System.Threading.Thread.Sleep(100);
                all.sync.Add(null);
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

                if (sCommand == "exit")
                    break;
            }

        }

    }
}
