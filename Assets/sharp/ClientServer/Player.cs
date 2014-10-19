﻿using System;
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
    public struct PlayerInfo
    {
        public Guid id;
        public OverlayEndpoint playerHost;
        public OverlayEndpoint validatorHost;
        public string name;

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
        OverlayHost myHost;

        GameInfo gameInfo;
        
        PlayerInfo info;

        PlayerData playerData = new PlayerData(); 
        Inventory frozenInventory = new Inventory();
        bool frozenSpawn = false;

        public PlayerValidator(PlayerInfo info_, GlobalHost globalHost, GameInfo gameInfo_)
        {
            info = info_;
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
            n.SendMessage(MessageType.PLAYER_INFO_VAR, playerData, PlayerDataUpdate.INIT);
        }

        void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.SPAWN_REQUEST)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);
                OnSpawnRequest(inf, n, remoteActionId);
            }
            else if (mt == MessageType.SPAWN_FAIL)
                OnSpawnFail();
            else if (mt == MessageType.PLAYER_WORLD_MOVE)
            {
                WorldMove wm = Serializer.Deserialize<WorldMove>(stm);

                if (wm == WorldMove.LEAVE)
                {
                    Point newWorld = Serializer.Deserialize<Point>(stm);
                    OnPlayerLeave(newWorld);
                }
                else
                    OnPlayerNewRealm(inf);
            }
            else if (mt == MessageType.PICKUP_ITEM)
                OnPickupItem();
            else if (mt == MessageType.FREEZE_ITEM)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);
                OnFreezeItem(n, remoteActionId);
            }
            else if (mt == MessageType.UNFREEZE_ITEM)
                OnUnfreezeItem();
            else if (mt == MessageType.CONSUME_FROZEN_ITEM)
                OnConsumeFrozen();
            else
                throw new Exception(Log.StDump(info, inf, mt, "unexpected message"));
        }

        void OnSpawnRequest(WorldInfo inf, Node n, Guid remoteActionId)
        {
            if (playerData.connected || frozenSpawn)
            {
                RemoteAction.Fail(n, remoteActionId);
            }
            else
            {
                frozenSpawn = true;
                RemoteAction.Sucess(n, remoteActionId);
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
                myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_INFO_VAR, playerData, PlayerDataUpdate.JOIN_WORLD);
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
            myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_INFO_VAR, playerData, PlayerDataUpdate.INVENTORY_CHANGE);
        }
        void OnFreezeItem(Node n, Guid remoteActionId)
        {
            MyAssert.Assert(playerData.inventory.teleport >= 0);
            MyAssert.Assert(frozenInventory.teleport >= 0);

            if (playerData.inventory.teleport > frozenInventory.teleport)
            {
                ++frozenInventory.teleport;
                //Log.Dump("success", info, playerData, "frozen", frozenInventory);
                RemoteAction.Sucess(n, remoteActionId);
            }
            else
            {
                Log.Dump("fail", info, playerData, "frozen", frozenInventory);
                RemoteAction.Fail(n, remoteActionId);
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
            myHost.BroadcastGroup(Client.hostName, MessageType.PLAYER_INFO_VAR, playerData, PlayerDataUpdate.INVENTORY_CHANGE);
        }
    }

    class PlayerAgent
    {
        OverlayHost myHost;

        OverlayEndpoint serverHost;

        public PlayerInfo info;

        public PlayerAgent(PlayerInfo info_, GlobalHost globalHost, OverlayEndpoint serverHost_)
        {
            info = info_;
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
        
        public void Move(WorldInfo worldInfo, Point newPos, MoveValidity mv)
        {
            myHost.ConnectSendMessage(worldInfo.host, MessageType.MOVE_REQUEST, newPos, mv);
        }
    }
}
