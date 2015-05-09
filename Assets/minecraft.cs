using UnityEngine;
using System.Collections.Generic;
using System;
using System.Net;
using System.Linq;

using ServerClient;
using Network;
using Tools;

public class RotateSlowly : MonoBehaviour {
    void Start () {}
    void Update ()
    {
        transform.rotation *= Quaternion.AngleAxis (Time.deltaTime*50, new Vector3(0,0,1));
    }
}

public class minecraft : MonoBehaviour
{
    private Aggregator all;

    private Guid me;
    private PlayerAgent myAgent = null;
    private PlayerData myData = null;

    private HashSet<WorldDraw> worlds = new HashSet<WorldDraw>();

    void OnGUI()
    {
        if (myData == null || !myData.IsConnected)
            return;

        GUI.Label(new Rect(10, 10, 100, 20), myData.WorldPosition.ToString());// + " " + myData.inventory.teleport);
        GUI.Label(new Rect(10, 30, 100, 20), all.myClient.serverHost.ToString());
    }

    static internal Vector3 GetPositionAtGrid(Point worldPos, Point pos)
    {
        Point cornerPos = Point.Scale(worldPos, World.worldSize);
        return new Vector3(cornerPos.x + pos.x, cornerPos.y + pos.y, 0);
    }
    static internal Point GetPositionAtMap(Point worldPos, Point pos)
    {
        Point cornerPos = Point.Scale(worldPos, World.worldSize);
        return pos - cornerPos;
    }

    const float cameraDistance = 30f;


    // Use this for initialization
    void Start()
    {
        MasterLog.Initialize("log_config.xml", msg => Debug.Log(msg));

        GameConfig cfg = GameConfig.ReadConfig("unity_config.xml");
        GameInstanceConifg cfg_local = cfg.clientConfig;

        IPAddress myIP = GameConfig.GetIP(cfg);

        Action<Guid, GameObject> updateCameraAction = (id, obj) => 
            {
                if(id == me)
                    UpdateCamera(obj);
            };

        Action<WorldDraw> onWorldDestruction = wd =>
            {
                MyAssert.Assert(worlds.Contains(wd));
                worlds.Remove(wd);
            };

        Func<WorldInitializer, World> newWorldCreation = (init) =>
            {
                bool isOwnedByUs = all.worldValidators.ContainsKey(init.info.position);

                WorldDraw wd = new WorldDraw(init, isOwnedByUs, updateCameraAction, onWorldDestruction);

                bool conflict = worlds.Where(w => w.Position == wd.Position).Any();
                MyAssert.Assert(!conflict);
                worlds.Add(wd);

                return wd;
            };

        all = new Aggregator(myIP, newWorldCreation);

        bool myServer = cfg.startServer && all.host.MyAddress.Port == GlobalHost.nStartPort;

        if (myServer)
            cfg_local = cfg.serverConfig;

        Program.MeshConnect(all, cfg, myIP);

        all.myClient.onServerReadyHook = () =>
        {
            if (cfg_local.validate)
                all.myClient.Validate();

            if (!myServer && cfg_local.aiPlayers > 0)
            {
                for (int i = 0; i < cfg_local.aiPlayers; ++i)
                    Program.NewAiPlayer(all);
            }

            all.myClient.NewWorld(new Point(0, 0));

            me = Guid.NewGuid();
            Log.Console("Player {0}", me);
            all.myClient.NewMyPlayer(me);
        };

        all.onNewPlayerAgentHook = (pa) =>
        {
            if (pa.Info.id == me)
            {
                myAgent = pa;

                pa.onDataHook = (pd) =>
                {
                    bool newData = (myData == null);

                    myData = pd;

                    if (newData)
                        TrySpawn();
                };
            }
        };

        if (myServer)
            all.StartServer(cfg.serverSpawnDensity);

        var light = gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = cameraDistance * 1.5f;
        light.intensity = 5;

        //camera.isOrthoGraphic = true;
        //camera.orthographicSize = 10;

        Application.runInBackground = true;
    }

    bool TrySpawn()
    {
        try
        {
            if (myData.IsConnected)
                return false;

            myAgent.Spawn();

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    void OnApplicationQuit()
    {
        Log.Console("Terminating");
        all.host.Close();
        System.Threading.Thread.Sleep(100);
        Log.Console(ThreadManager.Status());
        ThreadManager.Terminate();
        //System.Threading.Thread.Sleep (100);
        //all.sync.Add(null);
    }

    public void UpdateCamera(GameObject ourPlayer)
    {
        camera.transform.position = ourPlayer.transform.position;
        camera.transform.position += new Vector3(0f, 0f, -cameraDistance);
    }

    Dictionary<KeyCode, float> keyTrack = new Dictionary<KeyCode, float>();

    Dictionary<KeyCode, Point> keyDir = new Dictionary<KeyCode, Point>()
    { 
        {KeyCode.UpArrow, new Point(0, 1)},
        {KeyCode.DownArrow, new Point(0, -1)},
        {KeyCode.LeftArrow, new Point(-1, 0)},
        {KeyCode.RightArrow, new Point(1, 0)}
    };

    Point RayCast(Point currentWorldPos)
    {
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane xy = new Plane(Vector3.forward, new Vector3(0, 0, .5f));
        float distance;
        xy.Raycast(ray, out distance);
        var tile = ray.GetPoint(distance) + new Vector3(0, 0, 0);
        Point pos = new Point(Convert.ToInt32(tile.x), Convert.ToInt32(tile.y));
        pos = GetPositionAtMap(currentWorldPos, pos);

        return pos;
    }

    void ProcessMovement()
    {
        Point p = Point.Zero;

        foreach (KeyCode k in keyDir.Keys)
        {
            if (Input.GetKeyDown(k))
            {
                if (keyTrack.ContainsKey(k))
                    keyTrack.Remove(k);

                keyTrack.Add(k, 0);
                p += keyDir[k];
            }

            if (Input.GetKeyUp(k))
            {
                if (keyTrack.ContainsKey(k))
                    keyTrack.Remove(k);
            }
        }

        foreach (KeyCode k in keyTrack.Keys.ToArray())
        {
            keyTrack[k] += Time.deltaTime;
            if (keyTrack[k] >= .2f)
            {
                keyTrack[k] = 0;
                p += keyDir[k];
            }
        }

        bool teleport = Input.GetMouseButtonDown(0);
        bool move = (p != Point.Zero);

        if (!(move || teleport))
            return;

        PlayerAgent pa = myAgent;
        PlayerData pd = myData;
        if (pa == null || pd == null || !pd.IsConnected)
            return;

        World w = all.myClient.worlds.TryGetWorld(pd.WorldPosition);

        if (w == null)
            return;
        if (!w.HasPlayer(me))
            return;

        if (move)
        {
            Point oldPos = w.GetPlayerPosition(me);
            Point newPos = oldPos + p;

            ActionValidity mv = w.CheckValidMove(me, newPos);

            if (mv == ActionValidity.VALID || mv == ActionValidity.BOUNDARY)
                pa.Move(w.Info, newPos, mv);
        }
        else if (teleport)
        {
            Point pos = RayCast(w.Position);

            //Log.LogWriteLine("Teleporting to {0}", pos);
            pa.Move(w.Info, pos, ActionValidity.REMOTE);
            //pa.Move(all.myClient.gameInfo.GetWorldByPos(Point.Zero), pos, ActionValidity.REMOTE);
        }
    }
    void ProcessBlockInteraction()
    {
        if (!Input.GetMouseButtonDown(1) && !Input.GetMouseButtonDown(2))
            return;

        if (myData == null || !myData.IsConnected)
            return;

        World w = all.myClient.worlds.TryGetWorld(myData.WorldPosition);

        Point pos = RayCast(w.Position);

        if (Input.GetMouseButtonDown(1))
            myAgent.PlaceBlock(w.Info, pos);
        else
            myAgent.TakeBlock(w.Info, pos);
    }

    void ProcessQueuedMessages()
    {
        Queue<Action> actions = all.sync.TakeAll();

        while (actions.Any())
        {
            Action a = actions.Dequeue();
            if (a != null)
                a.Invoke();
        }
    }

    // Update is called once per frame
    void Update()
    {

        lock (all.sync.syncLock)
        {
            ProcessQueuedMessages();

            ProcessMovement();
            ProcessBlockInteraction();

            if (Input.GetKeyDown(KeyCode.S))
            {
                TrySpawn();
            }
            //camera.transform.rotation *= Quaternion.AngleAxis(Time.deltaTime * 1, Vector3.forward);
        }

        foreach (var w in worlds)
            w.TickAnimations(Time.deltaTime);
    }
}