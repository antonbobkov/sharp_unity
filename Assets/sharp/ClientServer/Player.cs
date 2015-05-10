using System;
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
    public enum NodeRole { CLIENT, SERVER, PLAYER_AGENT, PLAYER_VALIDATOR, WORLD_VALIDATOR };

    static class BasicInfo
    {
        static public MemoryStream GenerateHandshake(NodeRole nr, params object[] info)
        {
            MemoryStream ms = new MemoryStream();

            List<object> args = new List<object>();
            args.Add(nr);
            args.AddRange(info);

            Serializer.Serialize(ms, args.ToArray());

            return ms;
        }

    }

    [Serializable]
    public struct PlayerInfo
    {
        public Guid id;
        public OverlayEndpoint playerHost;
        public OverlayEndpoint validatorHost;
        public string name;
        public int generation;

        public PlayerInfo(Guid id, OverlayEndpoint playerHost, OverlayEndpoint validatorHost, string name, int generation)
        {
            this.id = id;
            this.playerHost = playerHost;
            this.validatorHost = validatorHost;
            this.name = name;
            this.generation = generation;
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
            return "tel: " + teleport + " blc: " + blocks;
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
        private OverlayHost myHost;

        private PlayerInfo info;
        private Node playerAgentNode = null;

        private PlayerData playerData;

        private RemoteAction locked = null;
        private Queue<Action> delayedActions = new Queue<Action>();

        private OverlayEndpoint serverHost;

        private bool finalizing = false;

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

        public PlayerValidator(PlayerInfo info, GlobalHost globalHost, OverlayEndpoint serverHost, PlayerData playerData)
        {
            this.info = info;
            this.serverHost = serverHost;
            this.playerData = playerData;

            myHost = globalHost.NewHost(info.validatorHost.hostname, Game.Convert(AssignProcessor),
                BasicInfo.GenerateHandshake(NodeRole.PLAYER_VALIDATOR, info), Aggregator.longInactivityWait);
            myHost.onNewConnectionHook = ProcessNewConnection;
        }

        GameNodeProcessors AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (n.info.remote == serverHost)
                return new GameNodeProcessors(
                    (mt, stm, nd) => { throw new Exception(Log.StDump(nd.info, mt, role, "unexpected")); },
                    ServerDisconnect);

            if (role == NodeRole.WORLD_VALIDATOR)
            {
                WorldInfo inf = Serializer.Deserialize<WorldInfo>(nodeInfo);
                return new GameNodeProcessors(
                    (mt, stm, nd) => ProcessWorldMessage(mt, stm, nd, inf),
                    WorldDisconnect);
            }

            if (role == NodeRole.PLAYER_AGENT)
                return new GameNodeProcessors((mt, stm, nd) => { throw new Exception(Log.StDump(nd.info, mt, role, "unexpected")); },
                    PlayerDisconnect);

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
                if (finalizing)
                {
                    MyAssert.Assert(locked == null);
                    OnFinalizedVerifier();
                }
            }
            else
            {
                if (finalizing) {
                    Log.Dump(info, " finalizing - ignored message:", mt);
                    return;
                }

                if (mt == MessageType.PICKUP_TELEPORT)
                    ProcessOrDelay(OnPickupTeleport);
                else if (mt == MessageType.PICKUP_BLOCK)
                    ProcessOrDelay(OnPickupBlock);
                else if (mt == MessageType.PLAYER_DISCONNECT)
                    ProcessOrDelay(OnDisconnect);
                else
                    throw new Exception(Log.StDump(info, inf, mt, "unexpected message"));
            }
        }

        void ServerDisconnect(NodeDisconnectInfo di) { }
        void PlayerDisconnect(NodeDisconnectInfo di) { }
        void WorldDisconnect(NodeDisconnectInfo di) { }

        void OnLock(Node n, Guid remoteActionId)
        {
            //Log.Dump();
            
            if (locked != null)
            {
                Log.Dump(info, n.info.remote, "already locked");
                RemoteAction.Fail(n, remoteActionId);
                return;
            }

            if (finalizing)
            {
                Log.Dump(info, n.info.remote, "cannot lock, finializing");
                RemoteAction.Fail(n, remoteActionId);
                return;
            }

            RemoteAction
                .Send(myHost, n.info.remote, null, MessageType.RESPONSE, Response.SUCCESS, remoteActionId, new RemoteActionIdInject(), playerData)
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
        void OnDisconnect()
        {
            playerData.world = null;
            MessageToAgent(MessageType.PLAYER_INFO_VAR, playerData);
        }

        public void FinalizeVerifier()
        {
            MyAssert.Assert(finalizing == false);
            finalizing = true;

            if (locked == null)
                OnFinalizedVerifier();
        }

        void OnFinalizedVerifier()
        {
            MyAssert.Assert(finalizing == true);
            MyAssert.Assert(locked == null);

            if (playerData.IsConnected)
            {
                myHost.TryConnectAsync(playerData.world.Value.host, WorldDisconnect);
                myHost.SendMessage(playerData.world.Value.host, MessageType.PLAYER_DISCONNECT);
                OnDisconnect();
            }

            myHost.TryConnectAsync(serverHost, ServerDisconnect);
            myHost.SendMessage(serverHost, MessageType.PLAYER_HOST_DISCONNECT, playerData);
        }
    }

    class PlayerAgent
    {
        private OverlayHost myHost;
        private OverlayEndpoint serverHost;
        private Client myClient;
        private bool active = true;

        public PlayerInfo Info { get; private set; }
        public PlayerData Data { get; private set; }

        public Action<PlayerData> onDataHook = (a) => { };

        public PlayerAgent(PlayerInfo info, GlobalHost globalHost, OverlayEndpoint serverHost, Client myClient)
        {
            this.Info = info;
            this.serverHost = serverHost;
            this.myClient = myClient;
            this.Data = null;

            myHost = globalHost.NewHost(info.playerHost.hostname, Game.Convert(AssignProcessor),
                BasicInfo.GenerateHandshake(NodeRole.PLAYER_AGENT, info), Aggregator.longInactivityWait);

            myHost.ConnectAsync(info.validatorHost, PlayerValidatorDisconnect);

            Log.Console("Player Agent {0}", info.GetShortInfo());
        }

        private GameNodeProcessors AssignProcessor(Node n, MemoryStream nodeInfo)
        {
            NodeRole role = Serializer.Deserialize<NodeRole>(nodeInfo);

            if (n.info.remote == serverHost)
                return new GameNodeProcessors(
                    (mt, stm, nd) => { throw new Exception(Log.StDump(mt, nd.info, "unexpected")); },
                    ServerDisconnect);
            
            if (n.info.remote == Info.validatorHost)
                return new GameNodeProcessors(
                    (mt, stm, nd) => ProcessPlayerValidatorMessage(mt, stm, nd),
                    PlayerValidatorDisconnect);

            if (role == NodeRole.WORLD_VALIDATOR)
                return new GameNodeProcessors(
                    (mt, stm, nd) => { throw new Exception(Log.StDump(mt, nd.info, "unexpected")); },
                    WorldDisconnect);

            throw new Exception(Log.StDump(n.info, role, "unexpected"));
        }
        private void ProcessPlayerValidatorMessage(MessageType mt, Stream stm, Node n)
        {
            //if (n.info.remote != info.validatorHost)
            //{
            //    Log.Dump(mt, "ignored", n.info.remote, info.validatorHost);
            //    return;
            //}

            if (!active)
            {
                Log.Dump(mt, "ignored - not active");
                return;
            }
            
            if (mt == MessageType.PLAYER_INFO_VAR)
            {
                PlayerData pd = Serializer.Deserialize<PlayerData>(stm);
                OnPlayerData(pd);
            }
            else
                throw new Exception(Log.StDump(mt, "unexpected"));
        }
        private void OnPlayerData(PlayerData pd)
        {
            try
            {
                onDataHook(pd);
                
                if (Data == null)   // initialize
                    return;

                if (Data.ToString() == pd.ToString())
                    throw new Exception(Log.StDump(Data, pd, "unchanged"));

                if (!Data.IsConnected) // spawn
                    OnSpawn(pd.world.Value);
                else if (!pd.IsConnected) // despawn
                { }
                else if (Data.world.Value.position != pd.world.Value.position)  // realm move
                    OnChangeRealm(Data.world.Value, pd.world.Value);

                if (Data.inventory != pd.inventory) // inventory change
                { }
            }
            finally
            {
                Data = pd;
            }
        }
        private void OnChangeRealm(WorldInfo oldWorld, WorldInfo newWorld)
        {
            //onRealmMoveHook(oldWorld, newWorld);
            myClient.connectedPlayers.Set(Info.id, newWorld);
        }
        private void OnSpawn(WorldInfo newWorld)
        {
            //onSpawnHook(newWorld);

            myClient.connectedPlayers.Set(Info.id, newWorld);
        }

        private void ServerDisconnect(NodeDisconnectInfo di) { }
        private void PlayerValidatorDisconnect(NodeDisconnectInfo di) { }
        private void WorldDisconnect(NodeDisconnectInfo di) { }

        public void Spawn()
        {
            myHost.TryConnectAsync(serverHost, ServerDisconnect);
            myHost.SendMessage(serverHost, MessageType.SPAWN_REQUEST);
        }
        public void Move(WorldInfo worldInfo, Point newPos, ActionValidity mv)
        {
            myHost.TryConnectAsync(worldInfo.host, WorldDisconnect);
            myHost.SendMessage(worldInfo.host, MessageType.MOVE_REQUEST, newPos, mv);
        }
        public void PlaceBlock(WorldInfo worldInfo, Point blockPos)
        {
            myHost.TryConnectAsync(worldInfo.host, WorldDisconnect);
            myHost.SendMessage(worldInfo.host, MessageType.PLACE_BLOCK, blockPos);
        }
        public void TakeBlock(WorldInfo worldInfo, Point blockPos)
        {
            myHost.TryConnectAsync(worldInfo.host, WorldDisconnect);
            myHost.SendMessage(worldInfo.host, MessageType.TAKE_BLOCK, blockPos);
        }
        //public void ChangeInfo(PlayerInfo inf)
        //{
        //    MyAssert.Assert(Info.generation < inf.generation);
        //    Info = inf;
        //    Data = null;
        //    myHost.ConnectAsync(inf.validatorHost);

        //    Log.Dump(inf);
        //}

        public void Deactivate()
        {
            MyAssert.Assert(active == true);
            active = false;
        }
    }
}
