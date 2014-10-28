﻿using UnityEngine;
using System.Collections.Generic;
using System;
using System.Net;
using System.Linq;

using ServerClient;

public class RotateSlowly : MonoBehaviour {
    void Start () {}
    void Update ()
    {
        transform.rotation *= Quaternion.AngleAxis (Time.deltaTime*50, new Vector3(0,0,1));
    }
}

class WorldDraw
{
    static public bool continuousBackground = false;

    World w;
    minecraft main;

    public readonly Point sz;

	public GameObject background;
	public Plane<GameObject> walls;
	public Plane<GameObject> loots;
    public Dictionary<Guid, GameObject> players = new Dictionary<Guid, GameObject>();

	public void MessBackground()
    {
        //background.transform.rotation = Quaternion.AngleAxis(2f, UnityEngine.Random.onUnitSphere);
        background.transform.localScale *= .99f;
    }

    public WorldDraw(World w_, minecraft main_)
	{
		w = w_;
        main = main_;
        sz = w.Size;

		walls = new Plane<GameObject>(sz);
		loots = new Plane<GameObject>(sz);
		
		foreach(Point pos in Point.Range(sz))
		{
			ITile t = w[pos];
			if (t.Solid)
			{
				GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
				walls [pos] = wall;
				
				wall.transform.position = minecraft.GetPositionAtGrid(w.Position, pos);
				if(!t.Spawn)
                    wall.renderer.material.color = new Color(.3f, .3f, .3f);
                else
                    wall.renderer.material.color = Color.yellow;
			}
			else if(t.Loot)
			{
				GameObject loot = GameObject.CreatePrimitive(PrimitiveType.Quad);
				loots [pos] = loot;
				
				loot.transform.localScale = new Vector3(.5f, .5f, 1);
				loot.renderer.material.color = Color.blue;
				loot.transform.position = minecraft.GetPositionAtGrid(w.Position, pos);
				loot.transform.rotation = Quaternion.AngleAxis(15f, UnityEngine.Random.onUnitSphere);
				loot.AddComponent<RotateSlowly>();
			}
		}

		background = GameObject.CreatePrimitive(PrimitiveType.Quad);
		background.transform.localScale = new Vector3(sz.x, sz.y, 1);
		background.transform.position = minecraft.GetPositionAtGrid(w.Position, new Point(0,0)) + new Vector3(sz.x-1, sz.y-1, 1)/2;
		background.renderer.material.color = new Color(.7f, .7f, .7f);

        if (!continuousBackground)
            MessBackground();
        
        foreach (PlayerInfo inf in w.GetAllPlayers())
		{
			AddPlayer(inf.id);
		}
    }

	public void Purge()
	{
		foreach(Point pos in Point.Range(sz))
		{
			UnityEngine.Object.Destroy (walls[pos]);
			UnityEngine.Object.Destroy (loots[pos]);
		}

        foreach(GameObject pl in players.Values)
            UnityEngine.Object.Destroy(pl);

		UnityEngine.Object.Destroy(background);
	}

    public void AddPlayer(Guid player)
    {
        if (players.ContainsKey(player))
            return;

		//MyAssert.Assert(w.playerPositions.ContainsKey(player));
        Point pos = w.GetPlayerPosition(player);

        var avatar = GameObject.CreatePrimitive(PrimitiveType.Sphere);

        var r = new ServerClient.MyRandom(BitConverter.ToInt32(player.ToByteArray(), 0));

        avatar.renderer.material.color = new Color(
            (float)r.NextDouble(),
            (float)r.NextDouble(),
            (float)r.NextDouble());

        avatar.transform.position = minecraft.GetPositionAtGrid(w.Position, pos);
        players.Add(player, avatar);

        if (player == main.me)
            main.UpdateCamera(avatar);
    }

    public void RemovePlayer(Guid player)
    {
        if (!players.ContainsKey(player))
            return;

        //MyAssert.Assert(w.playerPositions.ContainsKey(player));
        UnityEngine.Object.Destroy(players.GetValue(player));
		players.Remove(player);
    }
}

public class minecraft : MonoBehaviour {

    Aggregator all;

    //bool gameStarted = false;

	Dictionary<Point, WorldDraw> worlds = new Dictionary<Point, WorldDraw>();

	public Guid me;
    PlayerAgent myAgent = null;
    PlayerData myData = null;

    void OnGUI()
    {
        if (myData == null || !myData.IsConnected)
            return;

        GUI.Label(new Rect(10, 10, 100, 20), myData.WorldPosition.ToString());// + " " + myData.inventory.teleport);
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
	void Start () {
        Log.log = msg => Debug.Log(msg);

        all = new Aggregator();

        Program.MeshConnect(all);

        all.myClient.onServerReadyHook = () =>
        {
            if (all.host.MyAddress.Port == GlobalHost.nStartPort)
            {
                all.myClient.Validate();
                all.myClient.NewWorld(new Point(0, 0));
            }

            me = Guid.NewGuid();
            Log.LogWriteLine("Player {0}", me);
            all.myClient.NewMyPlayer(me);

            Program.NewAiPlayer(all);

        };

		all.onNewPlayerAgentHook = (pa) =>
		{
            if (pa.info.id == me)
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

        all.myClient.onMoveHook = (w, pl, pos, mt) => OnMove(w, pl, pos, mt);
        all.myClient.onPlayerLeaveHook = (w, pl) => OnPlayerLeave(w.Position, pl);
        
        all.myClient.onNewWorldHook = (w) => OnNewWorld(w);

        if (all.host.MyAddress.Port == GlobalHost.nStartPort)
            all.StartServer();

        //all.sync.Start();

        var light = gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = cameraDistance*1.5f;
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

    void OnApplicationQuit () {
		Log.LogWriteLine ("Terminating");
		all.host.Close();
		System.Threading.Thread.Sleep (100);
		Log.LogWriteLine (ThreadManager.Status ());
		ThreadManager.Terminate();
		//System.Threading.Thread.Sleep (100);
		//all.sync.Add(null);
	}

    void UpdateWorlds()
    {
        //if (myData == null || !myData.IsConnected)
        //    return;

        //suggestedPos = myData.WorldPosition;

        Dictionary<Point, WorldDraw> newWorlds = new Dictionary<Point, WorldDraw>();
        //foreach (Point delta in Point.SymmetricRange(new Point(1, 1)))
        //{
        //    Point targetPos = suggestedPos + delta;
        //    World worldCandidate = all.myClient.knownWorlds.TryGetValue(targetPos);
        foreach(World worldCandidate in all.myClient.knownWorlds.Values)
        {
            Point targetPos = worldCandidate.Position;

            if (worldCandidate == null)
                continue;

            if (worlds.ContainsKey(targetPos))
            {
                newWorlds[targetPos] = worlds[targetPos];
                worlds.Remove(targetPos);
            }
            else
                newWorlds[targetPos] = new WorldDraw(worldCandidate, this);
        }

        foreach (WorldDraw wdt in worlds.Values)
            wdt.Purge();
        worlds = newWorlds;
    }

    void OnNewWorld(World w)
    {
		UpdateWorlds();
        //Log.LogWriteLine("Unity : OnNewWorld " + w.Position);
    }

    void OnPlayerLeave(Point worldPos, PlayerInfo player)
    {
        WorldDraw worldDraw = worlds.TryGetValue(worldPos);
        if (worldDraw != null)
            worldDraw.RemovePlayer(player.id);
    }

    void OnMove(World w, PlayerInfo player, Point newPos, MoveValidity mv)
	{
		//Log.LogWriteLine(Log.Dump(this, w.info, player, player.id, mv));

        //if (mv == MoveValidity.NEW && player.id == me)
        //    UpdateWorlds(w.Position);

        if (!worlds.ContainsKey(w.Position))
            return;

        WorldDraw wd = worlds.GetValue(w.Position);

        if (mv == MoveValidity.NEW)
            wd.AddPlayer(player.id);

        GameObject obj = wd.loots[newPos];
		if (obj != null)
		{
			Destroy(obj);
            wd.loots[newPos] = null;
		}

        GameObject movedPlayer = wd.players.GetValue(player.id);
        movedPlayer.transform.position = GetPositionAtGrid(w.Position, newPos);

        if (player.id == me)
            UpdateCamera(movedPlayer);
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

        if (! (move || teleport))
            return;

        PlayerAgent pa = myAgent;
        PlayerData pd = myData;
        if (pa == null || pd == null || !pd.IsConnected)
            return;
        
        World w = all.myClient.knownWorlds.TryGetValue(pd.WorldPosition);

        if (w == null)
            return;
        if (!w.HasPlayer(me))
            return;

        if (move)
        {
            Point oldPos = w.GetPlayerPosition(me);
			Point newPos = oldPos + p;

            MoveValidity mv = w.CheckValidMove(me, newPos);
            
            if (mv == MoveValidity.VALID || mv == MoveValidity.BOUNDARY)
				pa.Move(w.Info, newPos, mv);
        }
        else if (teleport)
        {
			Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            Plane xy = new Plane(Vector3.forward, new Vector3(0, 0, .5f));
            float distance;
            xy.Raycast(ray, out distance);
            var tile = ray.GetPoint(distance) + new Vector3(0, 0, 0);
            Point pos = new Point(Convert.ToInt32(tile.x), Convert.ToInt32(tile.y));
			pos = GetPositionAtMap(w.Position, pos);
            Log.LogWriteLine("Teleporting to {0}", pos);
            pa.Move(w.Info, pos, MoveValidity.TELEPORT);
            //pa.Move(all.myClient.gameInfo.GetWorldByPos(Point.Zero), pos, MoveValidity.TELEPORT);
        }
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
	void Update () {

        lock (all.sync.syncLock)
        {
            ProcessQueuedMessages();

            ProcessMovement();

            if(Input.GetKeyDown(KeyCode.Alpha1))
            {
                if (WorldDraw.continuousBackground == true)
                {
                    WorldDraw.continuousBackground = false;
                    foreach (var w in worlds)
                        w.Value.MessBackground();
                }
            }

            if (Input.GetKeyDown(KeyCode.S))
            {
                TrySpawn();
            }
            //camera.transform.rotation *= Quaternion.AngleAxis(Time.deltaTime * 1, Vector3.forward);
        }
	}
}