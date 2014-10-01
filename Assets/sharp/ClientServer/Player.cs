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
    [Serializable]
    public class PlayerInfo
    {
        public Guid id;
        public OverlayEndpoint playerHost;
        public OverlayEndpoint validatorHost;
        public string name;

        public PlayerInfo() { }
        public PlayerInfo(Guid id_, OverlayEndpoint playerHost_, OverlayEndpoint validatorHost_, string name_)
        {
            id = id_;
            playerHost = playerHost_;
            validatorHost = validatorHost_;
            name = name_;
        }

        public override string ToString()
        {
            return GetFullInfo();
        }

        public string GetFullInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Player id: {0}\n", id);
            sb.AppendFormat("Player name: {0}\n", name);
            sb.AppendFormat("Player host: {0}\n", playerHost);
            sb.AppendFormat("Player validator: {0}\n", validatorHost);

            return sb.ToString();
        }
        public string GetShortInfo()
        {
            return "Player " + name;
        }
    }

    [Serializable]
    public class Inventory
    {
        public int teleport = 0;

        public Inventory() { }
        public Inventory(int teleport_) { teleport = teleport_; }

        public override string ToString()
        {
            return "teleport: " + teleport;
        }
    }

    [Serializable]
    public class PlayerData
    {
        public bool connected = false;
        public Point worldPos = new Point(0,0);

        public Inventory totalInventory = new Inventory(5);

        public override string ToString()
        {
            return (connected ? worldPos.ToString() : "[not connected]") + " " + totalInventory;
        }
    }

    class PlayerValidator
    {
        Action<Action> sync;
        OverlayHost myHost;

        GameInfo gameInfo;
        
        PlayerInfo info;

        PlayerData playerData = new PlayerData(); 
        Inventory frozenInventory = new Inventory();

        public PlayerValidator(PlayerInfo info_, Action<Action> sync_, GlobalHost globalHost, GameInfo gameInfo_)
        {
            info = info_;
            sync = sync_;
            gameInfo = gameInfo_;

            myHost = globalHost.NewHost(info.validatorHost.hostname, AssignProcessor);
            myHost.onNewConnectionHook = ProcessNewConnection;
        }
        
        Node.MessageProcessor AssignProcessor(Node n)
        {
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)
                return (mt, stm, nd) => { throw new Exception("PlayerValidator not expecting messages from Client." + mt + " " + nd.info); };

            NodeRole role = gameInfo.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = gameInfo.GetWorldByHost(n.info.remote);
                return (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf);
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
            n.SendMessage(MessageType.PLAYER_INFO, playerData);
        }

        void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
            {
                sync.Invoke(() => OnSpawnRequest(inf, n));
            }
            else
                throw new Exception("PlayerValidator.ProcessWorldMessage bad message type " + mt.ToString());
        }

        void OnSpawnRequest(WorldInfo inf, Node n)
        {
            if (playerData.connected)
            {
                n.SendMessage(MessageType.SPAWN_FAIL);
            }
            else
            {
                playerData.connected = true;
                playerData.worldPos = inf.worldPos;

                n.SendMessage(MessageType.SPAWN_SUCCESS);
                myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_INFO, playerData);
            }
        }
    }

    class PlayerAgent
    {
        Action<Action> sync;
        OverlayHost myHost;

        GameInfo gameInfo;

        public PlayerInfo info;

        public PlayerAgent(PlayerInfo info_, Action<Action> sync_, GlobalHost globalHost, GameInfo gameInfo_)
        {
            info = info_;
            sync = sync_;
            gameInfo = gameInfo_;

            myHost = globalHost.NewHost(info.playerHost.hostname, AssignProcessor);

            Log.LogWriteLine("Player Agent {0}", info.GetShortInfo());
        }

        Node.MessageProcessor AssignProcessor(Node n)
        {
            return (mt, stm, nd) => { throw new Exception("PlayerAgent is not expecting messages." + mt + " " + nd.info); };
            /*
            OverlayHostName remoteName = n.info.remote.hostname;
            if (remoteName == Client.hostName)

            NodeRole role = gameInfo.GetRoleOfHost(n.info.remote);

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = gameInfo.GetWorldByHost(n.info.remote);
                return (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf);
            }

            throw new InvalidOperationException("WorldValidator.AssignProcessor unexpected connection " + n.info.remote + " " + role);
            */
        }

        public void Spawn(Point worldPos)
        {
            WorldInfo w = gameInfo.GetWorldByPos(worldPos);
            myHost.ConnectSendMessage(w.host, MessageType.SPAWN_REQUEST);
        }
        public void Move(WorldInfo worldInfo, Point newPos)
        {
            myHost.ConnectSendMessage(worldInfo.host, MessageType.MOVE, newPos);
        }
    }
}
