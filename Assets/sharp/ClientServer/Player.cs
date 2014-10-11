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
            return GetShortInfo();
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

        public Inventory inventory = new Inventory(5);

        public override string ToString()
        {
            return (connected ? worldPos.ToString() : "[not connected]") + " " + inventory;
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
        bool frozenSpawn = false;

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
            n.SendMessage(MessageType.PLAYER_INFO, playerData, PlayerDataUpdate.NEW);
        }

        void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
                sync.Invoke(() => OnSpawnRequest(inf, n));
            else if (mt == MessageType.SPAWN_FAIL)
                sync.Invoke(OnSpawnFail);
            else if (mt == MessageType.PLAYER_JOIN)
                sync.Invoke(() => OnPlayerNewRealm(inf));
            else if (mt == MessageType.PLAYER_LEAVE)
            {
                Point newWorld = Serializer.Deserialize<Point>(stm);
                sync.Invoke(() => OnPlayerLeave(newWorld));
            }
            else if (mt == MessageType.PICKUP_ITEM)
                sync.Invoke(() => OnPickupItem());
            else if (mt == MessageType.FREEZE_ITEM)
                sync.Invoke(() => OnFreezeItem(n));
            else if (mt == MessageType.UNFREEZE_ITEM)
                sync.Invoke(() => OnUnfreezeItem());
            else if (mt == MessageType.CONSUME_FROZEN_ITEM)
                sync.Invoke(() => OnConsumeFrozen());
            else
                throw new Exception(Log.StDump(info, inf, mt, "unexpected message"));
        }

        void OnSpawnRequest(WorldInfo inf, Node n)
        {
            if (playerData.connected || frozenSpawn)
            {
                n.SendMessage(MessageType.SPAWN_FAIL);
            }
            else
            {
                frozenSpawn = true;
                n.SendMessage(MessageType.SPAWN_SUCCESS);
            }
        }
        void OnSpawnFail()
        {
            MyAssert.Assert(frozenSpawn);
            frozenSpawn = false;
        }

        void RealmUpdate(Point newWorld)
        {
            bool forceUpdate = false;
            
            if (!playerData.connected)
            {
                playerData.connected = true;
                frozenSpawn = false;
                forceUpdate = true;
            }

            if ((playerData.worldPos != newWorld) || forceUpdate)
            {
                playerData.worldPos = newWorld;
                myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_INFO, playerData, PlayerDataUpdate.JOIN);
            }
        }

        void OnPlayerNewRealm(WorldInfo inf)
        {
            RealmUpdate(inf.worldPos);
        }
        void OnPlayerLeave(Point newWorld)
        {
            RealmUpdate(newWorld);
        }
        void OnPickupItem()
        {
            ++playerData.inventory.teleport;
            //Log.Dump(info, playerData, "frozen", frozenInventory);
            myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_INFO, playerData, PlayerDataUpdate.INVENTORY);
        }
        void OnFreezeItem(Node n)
        {
            MyAssert.Assert(playerData.inventory.teleport >= 0);
            MyAssert.Assert(frozenInventory.teleport >= 0);

            if (playerData.inventory.teleport > frozenInventory.teleport)
            {
                ++frozenInventory.teleport;
                //Log.Dump("success", info, playerData, "frozen", frozenInventory);
                n.SendMessage(MessageType.FREEZE_SUCCESS);
            }
            else
            {
                //Log.Dump("fail", info, playerData, "frozen", frozenInventory);
                n.SendMessage(MessageType.FREEZE_FAIL);
            }
        }
        void OnUnfreezeItem()
        {
            --frozenInventory.teleport;
            MyAssert.Assert(frozenInventory.teleport >= 0);
            //Log.Dump(info, playerData, "frozen", frozenInventory);
        }
        void OnConsumeFrozen()
        {
            --frozenInventory.teleport;
            --playerData.inventory.teleport;

            MyAssert.Assert(playerData.inventory.teleport >= 0);
            MyAssert.Assert(frozenInventory.teleport >= 0);

            //Log.Dump(info, playerData, "frozen", frozenInventory);
            myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_INFO, playerData, PlayerDataUpdate.INVENTORY);
        }
    }

    class PlayerAgent
    {
        Action<Action> sync;
        OverlayHost myHost;

        GameInfo gameInfo;
        OverlayEndpoint serverHost;

        public PlayerInfo info;

        public PlayerAgent(PlayerInfo info_, Action<Action> sync_, GlobalHost globalHost, GameInfo gameInfo_, OverlayEndpoint serverHost_)
        {
            info = info_;
            sync = sync_;
            gameInfo = gameInfo_;
            serverHost = serverHost_;

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

        public void Spawn()
        {
            myHost.ConnectSendMessage(serverHost, MessageType.SPAWN_REQUEST);
        }
        
        public void Move(WorldInfo worldInfo, Point newPos, MessageType mt)
        {
            myHost.ConnectSendMessage(worldInfo.host, mt, newPos);
        }
    }
}
