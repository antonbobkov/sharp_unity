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
    class DelayedAction
    {
        public OverlayEndpoint ep;
        public Action a;
    }

    class Server
    {
        public static readonly OverlayHostName hostName = new OverlayHostName("server");

        Random r = new Random();

        GameInfo gameState = new GameInfo();
        List<IPEndPoint> validatorPool = new List<IPEndPoint>();

        Action<Action> sync;
        OverlayHost myHost;

        int playerCounter = 1;

        Dictionary<Guid, DelayedAction> delayedActions = new Dictionary<Guid, DelayedAction>();

        public OverlayEndpoint Address { get { return myHost.Address; } }

        public Server(Action<Action> sync_, GlobalHost globalHost)
        {
            sync = sync_;

            myHost = globalHost.NewHost(Server.hostName, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;
        }

        Node.MessageProcessor AssignProcessor(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)
                return ProcessClientMessage;

            throw new InvalidOperationException("Server.AssignProcessor unexpected connection " + remoteName.ToString());
        }
        void ProcessClientMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.NEW_PLAYER)
            {
                Guid player = Serializer.Deserialize<Guid>(stm);
                sync.Invoke(() => OnNewPlayerRequest(player, n.info.remote));
            }
            else if (mt == MessageType.NEW_WORLD)
            {
                Point worldPos = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnNewWorldRequest(worldPos));
            }
            else if (mt == MessageType.NEW_VALIDATOR)
            {
                sync.Invoke(() => OnNewValidator(n.info.remote.addr));
            }
            else if (mt == MessageType.ACCEPT)
            {
                Guid actionId = Serializer.Deserialize<Guid>(stm);
                sync.Invoke(() => OnAccept(actionId, n.info.remote));
            }
            else
                throw new Exception("Client.ProcessClientMessage bad message type " + mt.ToString());
        }

        void ProcessNewConnection(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;

            if (remoteName == Client.hostName)
                OnNewClient(n);
            else
                throw new InvalidOperationException("Server.ProcessNewConnection unexpected connection " + remoteName.ToString());
        }
        void OnNewClient(Node n)
        {
            n.SendMessage(MessageType.GAME_INFO, gameState.Serialize());
        }

        void OnNewPlayerRequest(Guid playerId, OverlayEndpoint playerHost)
        {
            Guid validatorId = Guid.NewGuid();
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName(validatorId.ToString()));

            OverlayEndpoint playerNewHost = new OverlayEndpoint(playerHost.addr, new OverlayHostName(playerId.ToString()));
            PlayerInfo info = new PlayerInfo(playerId, playerNewHost, validatorHost, (playerCounter++).ToString());

            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);
            myHost.SendMessage(validatorClient, MessageType.PLAYER_VALIDATOR_ASSIGN, validatorId, info);

            DelayedAction da = new DelayedAction()
            {
                ep = validatorClient,
                a = () =>
                {
                    gameState.AddPlayer(info);
                    myHost.BroadcastGroup(Client.hostName, MessageType.NEW_PLAYER, info);
                }
            };

            delayedActions.Add(validatorId, da);
        }
        void OnNewValidator(IPEndPoint ip)
        {
            MyAssert.Assert(!validatorPool.Where((valip) => valip == ip).Any());
            validatorPool.Add(ip);
        }
        void OnNewWorldRequest(Point worldPos)
        {
            Guid validatorId = Guid.NewGuid();
            OverlayEndpoint validatorHost = new OverlayEndpoint(validatorPool.Random(n => r.Next(n)), new OverlayHostName(validatorId.ToString()));

            WorldInfo info = new WorldInfo(worldPos, validatorHost);
            WorldInitializer init = new WorldInitializer(r.Next(), .2, .05);

            OverlayEndpoint validatorClient = new OverlayEndpoint(validatorHost.addr, Client.hostName);
            myHost.SendMessage(validatorClient, MessageType.WORLD_VALIDATOR_ASSIGN, validatorId, info, init);

            DelayedAction da = new DelayedAction()
            {
                ep = validatorClient,
                a = () =>
                {
                    gameState.AddWorld(info);
                    myHost.BroadcastGroup(Client.hostName, MessageType.NEW_WORLD, info);
                }
            };

            delayedActions.Add(validatorId, da);

        }

        void OnAccept(Guid id, OverlayEndpoint remote)
        {
            DelayedAction da = delayedActions.GetValue(id);
            delayedActions.Remove(id);

            MyAssert.Assert(da.ep == remote);

            da.a();
        }
    }
}
