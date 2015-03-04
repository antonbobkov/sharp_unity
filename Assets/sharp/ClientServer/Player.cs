﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Xml.Serialization;

using Tools;
using Network;


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
        public int teleport;
        public int blocks;

        public Inventory() { }
        public Inventory(int teleport_, int blocks_) { teleport = teleport_; blocks = blocks_; }

        public override string ToString()
        {
            return "tel: " + teleport + "blc: " + blocks;
        }
    }

    [Serializable]
    public class PlayerData
    {
        [XmlIgnore]
        public bool IsConnected { get { return world != null; } }

        [XmlIgnore]
        public Point WorldPosition { get { return world.Value.position; } }

        public WorldInfo? world = null;

        public Inventory inventory = new Inventory(5, 5);

        public override string ToString()
        {
            return (IsConnected ? WorldPosition.ToString() : "[not connected]") + " " + inventory;
        }
    }

    class PlayerValidator
    {
        OverlayHost myHost;

        PlayerInfo info;
        Node playerAgentNode = null;

        PlayerData playerData = new PlayerData(); 

        RemoteAction locked = null;
        Queue<Action> delayedActions = new Queue<Action>();

        void ProcessOrDelay(Action a)
        {
            if (locked == null)
                a.Invoke();
            else
            {
                //Log.Dump(info, "delayed action");
                delayedActions.Enqueue(a);
            }
        }
        void ExecuteDelayedActions()
        {
            while (delayedActions.Any())
            {
                //Log.Dump(info, "executed delayed action");
                delayedActions.Dequeue().Invoke();
            }
        }

        public PlayerValidator(PlayerInfo info_, GlobalHost globalHost)
        {
            info = info_;

            myHost = globalHost.NewHost(info.validatorHost.hostname, AssignProcessor,
                OverlayHost.GenerateHandshake(NodeRole.PLAYER_VALIDATOR, info));
            myHost.onNewConnectionHook = ProcessNewConnection;
        }

        Node.MessageProcessor AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);
    
            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = Serializer.Deserialize<WorldInfo>(nodeInfo);
                return (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf);
            }

            if (role == NodeRole.PLAYER_AGENT)
                return (mt, stm, nd) => { throw new Exception(Log.StDump(nd.info, mt, role, "unexpected")); };

            throw new Exception(Log.StDump(n.info, role, "unexpected"));
        }

        void ProcessNewConnection(Node n)
        {
            if (n.info.remote == info.playerHost)
                OnAgentConnect(n);
        }
        void OnAgentConnect(Node n)
        {
            playerAgentNode = n;
            MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData);
        }

        void MessageToAgent(MessageType mt, params object[] args)
        {
            if (playerAgentNode == null)
                return;

            playerAgentNode.SendMessage(mt, args);
        }

        void ProcessWorldMessage(MessageType mt, Stream stm, Node n, WorldInfo inf)
        {
            if (mt == MessageType.LOCK_VAR)
            {
                Guid remoteActionId = Serializer.Deserialize<Guid>(stm);
                OnLock(n, remoteActionId);
            }
            else if (mt == MessageType.UNLOCK_VAR)
            {
                RemoteAction.Process(ref locked, n, stm);
            }
            else if (mt == MessageType.PICKUP_TELEPORT)
                ProcessOrDelay(() => OnPickupTeleport());
            else if (mt == MessageType.PICKUP_BLOCK)
                ProcessOrDelay(() => OnPickupBlock());
            else
                throw new Exception(Log.StDump(info, inf, mt, "unexpected message"));
        }

        void OnLock(Node n, Guid remoteActionId)
        {
            //Log.Dump();
            
            if (locked != null)
            {
                Log.Dump(info, "already locked");
                RemoteAction.Fail(n, remoteActionId);
                return;
            }

            RemoteAction
                .Send(myHost, n.info.remote, MessageType.RESPONSE, Response.SUCCESS, remoteActionId, new RemoteActionIdInject(), playerData)
                .Respond(ref locked, (res, str) =>
                {
                    //Log.Dump("unlocking", res);

                    if (res == Response.SUCCESS)
                    {
                        PlayerData modifiedData = Serializer.Deserialize<PlayerData>(str);

                        if (playerData.ToString() == modifiedData.ToString())
                            throw new Exception(Log.StDump(playerData, n.info, "unchanged"));

                        //Log.Dump(info.name, "from ", playerData);
                        playerData = modifiedData;
                        //Log.Dump(info.name, "to ", playerData);
                        //Log.Dump(info, "unlocking got new data", pdu);
                        MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData);
                    }
                    //else
                    //    Log.Dump(info, "unlocking - unchanged");

                    ExecuteDelayedActions();
                });
        }
        
        void OnPickupTeleport()
        {
            ++playerData.inventory.teleport;

            MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData);
        }
        void OnPickupBlock()
        {
            ++playerData.inventory.blocks;

            MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData);
        }
    }

    class PlayerAgent
    {
        OverlayHost myHost;

        OverlayEndpoint serverHost;

        Client myClient;

        public readonly PlayerInfo info;
        public PlayerData data = null;

        public Action<PlayerData> onDataHook = (a) => { };
        public Action<WorldInfo> onSpawnHook = (a) => { };
        public Action<WorldInfo, WorldInfo> onRealmMoveHook = (a, b) => { };

        public PlayerAgent(PlayerInfo info_, GlobalHost globalHost, OverlayEndpoint serverHost_, Client myClient_)
        {
            info = info_;
            serverHost = serverHost_;
            myClient = myClient_;

            myHost = globalHost.NewHost(info.playerHost.hostname, AssignProcessor,
                OverlayHost.GenerateHandshake(NodeRole.PLAYER_AGENT, info));

            myHost.ConnectAsync(info.validatorHost);

            Log.Console("Player Agent {0}", info.GetShortInfo());
        }

        Node.MessageProcessor AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (n.info.remote == serverHost)
                return (mt, stm, nd) => { throw new Exception(Log.StDump(mt, nd.info, "unexpected")); };
            
            if (n.info.remote == info.validatorHost)
                return (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd);
            
            if (role == NodeRole.WORLD_VALIDATOR)
                return (mt, stm, nd) => { throw new Exception(Log.StDump(mt, nd.info, "unexpected")); };

            throw new Exception(Log.StDump(n.info, role, "unexpected"));
        }

        void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n)
        {
            if (mt == MessageType.PLAYER_INFO_VAR)
            {
                PlayerData pd = Serializer.Deserialize<PlayerData>(stm);
                OnPlayerData(pd);
            }
            else
                throw new Exception(Log.StDump(mt, "unexpected"));
        }

        void OnPlayerData(PlayerData pd)
        {
            try
            {
                onDataHook(pd);
                
                if (data == null)   // initialize
                    return;

                if (data.ToString() == pd.ToString())
                    throw new Exception(Log.StDump(data, pd, "unchanged"));

                if (!data.IsConnected) // spawn
                    OnSpawn(pd.world.Value);
                else if (data.world.Value.position != pd.world.Value.position)  // realm move
                    OnChangeRealm(data.world.Value, pd.world.Value);

                if (data.inventory != pd.inventory) // inventory change
                { }
            }
            finally
            {
                data = pd;
            }
        }

        void OnChangeRealm(WorldInfo oldWorld, WorldInfo newWorld)
        {
            onRealmMoveHook(oldWorld, newWorld);

            myClient.TrackWorld(newWorld);
            myClient.UnTrackWorld(oldWorld.position);
        }

        void OnSpawn(WorldInfo newWorld)
        {
            onSpawnHook(newWorld);

            myClient.TrackWorld(newWorld);
        }
        
        public void Spawn()
        {
            myHost.ConnectSendMessage(serverHost, MessageType.SPAWN_REQUEST);
        }
        public void Move(WorldInfo worldInfo, Point newPos, ActionValidity mv)
        {
            myHost.ConnectSendMessage(worldInfo.host, MessageType.MOVE_REQUEST, newPos, mv);
        }
        public void PlaceBlock(WorldInfo worldInfo, Point blockPos)
        {
            myHost.ConnectSendMessage(worldInfo.host, MessageType.PLACE_BLOCK, blockPos);
        }
        public void TakeBlock(WorldInfo worldInfo, Point blockPos)
        {
            myHost.ConnectSendMessage(worldInfo.host, MessageType.TAKE_BLOCK, blockPos);
        }
    }
}
