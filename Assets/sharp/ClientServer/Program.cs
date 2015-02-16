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
using System.Xml.Serialization;

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
        static public void MeshConnect(Aggregator all)
        {
            all.sync.Add(() =>
                {
                    IPEndPoint ep = Aggregator.ParseParamForIP(new List<string>());
                    if(all.myClient.TryConnect(ep))
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
                        IPEndPoint ep = Aggregator.ParseParamForIP(ls);
                        if (all.myClient.TryConnect(ep))
                            Log.LogWriteLine("Connecting to {0} {1}", ep.Address, ep.Port);
                    });
                }
            }
            catch (Exception){}
        }

        //public static void GameInfoOut(GameInfo inf)
        //{
        //    GameInfoSerialized infoser = inf.Serialize();
        //    foreach (WorldInfo pi in infoser.worlds)
        //        Console.WriteLine(pi.GetFullInfo());
        //}

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
            
            Aggregator all = new Aggregator();

            MeshConnect(all);

            all.myClient.onServerReadyHook = () =>
            {
                all.myClient.Validate();
                if (all.host.MyAddress.Port != GlobalHost.nStartPort)
                {
                    for (int i = 0; i < 10; ++i)
                        NewAiPlayer(all);
                }
                else
                {
                    all.myClient.NewWorld(new Point(0, 0));
                }
            };

            if (all.host.MyAddress.Port == GlobalHost.nStartPort)
                all.StartServer();

            all.sync.Start();

            InputProcessor inputProc = new InputProcessor(all.sync.GetAsDelegate());

            inputProc.commands.Add("connect", (param) => all.ParamConnect(param));

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
        }

    }
}
