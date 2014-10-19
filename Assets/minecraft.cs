using UnityEngine;
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
    static public bool continuousBackground = true;

    World w;

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

    public WorldDraw(World w_)
	{
		w = w_;
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
				
				wall.transform.position = minecraft.GetPositionAtGrid(w, pos);
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
				loot.transform.position = minecraft.GetPositionAtGrid(w, pos);
				loot.transform.rotation = Quaternion.AngleAxis(15f, UnityEngine.Random.onUnitSphere);
				loot.AddComponent<RotateSlowly>();
			}
		}

		background = GameObject.CreatePrimitive(PrimitiveType.Quad);
		background.transform.localScale = new Vector3(sz.x, sz.y, 1);
		background.transform.position = minecraft.GetPositionAtGrid(w, new Point(0,0)) + new Vector3(sz.x-1, sz.y-1, 1)/2;
		background.renderer.material.color = new Color(.7f, .7f, .7f);

        if (!continuousBackground)
            MessBackground();
        
        foreach (Guid id in w.GetAllPlayers())
		{
			Log.Dump(w.Info, id);
			AddPlayer(id);
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
		if(players.ContainsKey(player))
		{
			// due to syncronization fail, this player may be already initialized
			// this happens is world join is really close to world creation
			return;
		}

		//MyAssert.Assert(w.playerPositions.ContainsKey(player));
        Point pos = w.GetPlayerPosition(player);

        var avatar = GameObject.CreatePrimitive(PrimitiveType.Sphere);

        var r = new ServerClient.MyRandom(BitConverter.ToInt32(player.ToByteArray(), 0));

        avatar.renderer.material.color = new Color(
            (float)r.NextDouble(),
            (float)r.NextDouble(),
            (float)r.NextDouble());

        avatar.transform.position = minecraft.GetPositionAtGrid(w, pos);
        players.Add(player, avatar);
    }

    public void RemovePlayer(Guid player)
    {
        if(!players.ContainsKey(player))
        {
            // due to syncronization fail, this player may happen
            return;
        }

        //MyAssert.Assert(w.playerPositions.ContainsKey(player));
        UnityEngine.Object.Destroy(players.GetValue(player));
		players.Remove(player);
    }
}

public class minecraft : MonoBehaviour {

    Aggregator all;

    //bool gameStarted = false;

	Dictionary<Point, WorldDraw> worlds = new Dictionary<Point, WorldDraw>();

	Guid me;

    Queue<Action> bufferedActions = new Queue<Action>();

    static internal Vector3 GetPositionAtGrid(World w, Point pos)
    {
        Point worldPos = new Point(w.Position.x * w.Size.x, w.Position.y * w.Size.y);
		return new Vector3(worldPos.x + pos.x, worldPos.y + pos.y, 0);
    }
	static internal Point GetPositionAtMap(World w, Point pos)
	{
        Point worldPos = new Point(w.Position.x * w.Size.x, w.Position.y * w.Size.y);
		return pos - worldPos;
	}

    const float cameraDistance = 20f;

	// Use this for initialization
	void Start () {
        Log.log = msg => Debug.Log(msg);

        all = new Aggregator();

        Program.MeshConnect(all);

        all.myClient.hookServerReady = () =>
        {
            if (all.host.MyAddress.Port == GlobalHost.nStartPort)
            {
                all.myClient.Validate();
                all.myClient.NewWorld(new Point(0, 0));
            }

            me = Guid.NewGuid();
            Log.LogWriteLine("Player {0}", me);
            all.myClient.NewMyPlayer(me);

        };

        all.myClient.onNewPlayerHook = (inf) =>
        {
            if (inf.id == me)
                TrySpawn();
        };

		all.myClient.onNewPlayerDataHook = (inf, pd) =>
		{
			if (inf.id == me)
				TrySpawn();
		};

        all.myClient.onMoveHook = (w, pl, mt) => bufferedActions.Enqueue(() => OnMove(w, pl, mt));
        all.myClient.onNewWorldHook = (w) => bufferedActions.Enqueue(() => OnNewWorld(w));
		all.myClient.onPlayerNewRealm = (inf, pd) => bufferedActions.Enqueue(() => 
		{
			if(inf.id == me)
				UpdateWorlds();
		});

        if (all.host.MyAddress.Port == GlobalHost.nStartPort)
            all.StartServer();

        all.sync.Start();

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
            PlayerAgent pa = all.playerAgents.GetValue(me);
            all.myClient.knownWorlds.GetValue(new Point(0, 0));
            PlayerData pd = all.myClient.knownPlayers.GetValue(me);

            if (pd.connected)
                return false;

            pa.Spawn();

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
        Point centerWorldPos = new Point(0,0);

        PlayerData myData = all.myClient.knownPlayers.TryGetValue(me);
        if (myData != null)
        {
            if (myData.connected)
                centerWorldPos = myData.worldPos;
        }

        Dictionary<Point, WorldDraw> newWorlds = new Dictionary<Point, WorldDraw>();
        foreach (Point delta in Point.SymmetricRange(new Point(1, 1)))
        {
            Point targetPos = centerWorldPos + delta;
            World worldCandidate = all.myClient.knownWorlds.TryGetValue(targetPos);
            if (worldCandidate == null)
                continue;

            if (worlds.ContainsKey(targetPos))
            {
                newWorlds[targetPos] = worlds[targetPos];
                worlds.Remove(targetPos);
            }
            else
                newWorlds[targetPos] = new WorldDraw(worldCandidate);
        }

        foreach (WorldDraw wdt in worlds.Values)
            wdt.Purge();
        worlds = newWorlds;
    }

    void OnNewWorld(World w)
    {
		UpdateWorlds();
        Log.LogWriteLine("Unity : OnNewWorld " + w.Position);

        if (w.Position == new Point(0, 0))
            TrySpawn();
    }

    void OnMove(World w, PlayerInfo player, MoveType mv)
	{
		//Log.LogWriteLine(Log.Dump(this, w.info, player, player.id, mv));

		if (mv == MoveType.LEAVE)
        {
            WorldDraw worldDraw = worlds.TryGetValue(w.Position);
            if (worldDraw != null)
                worldDraw.RemovePlayer(player.id);
            return;
        }

        //if (mv == MoveType.JOIN && player.id == me)
        //    UpdateWorlds(w.Position);

        if (!worlds.ContainsKey(w.Position))
            return;

        WorldDraw wd = worlds.GetValue(w.Position);

        if (mv == MoveType.JOIN)
            wd.AddPlayer(player.id);

        Point pos = w.GetPlayerPosition(player.id);

		GameObject obj = wd.loots[pos];
		if (obj != null)
		{
			Destroy(obj);
			wd.loots[pos] = null;
		}

        MyAssert.Assert(wd.players.ContainsKey(player.id));

        GameObject movedPlayer = wd.players.GetValue(player.id);
		movedPlayer.transform.position = GetPositionAtGrid(w, pos);

        if (player.id == me)
            UpdateCamera(movedPlayer);
	}

    public void UpdateCamera(GameObject ourPlayer)
    {
        camera.transform.position = ourPlayer.transform.position;
        camera.transform.position += new Vector3(0f, 0f, -cameraDistance);
    }

	void ProcessMovement()
    {
        Point p = new Point();

        if (Input.GetKeyDown(KeyCode.UpArrow))
            p = p + new Point(0, 1);
        if (Input.GetKeyDown(KeyCode.DownArrow))
            p = p + new Point(0, -1);
        if (Input.GetKeyDown(KeyCode.LeftArrow))
            p = p + new Point(-1, 0);
        if (Input.GetKeyDown(KeyCode.RightArrow))
            p = p + new Point(1, 0);

        bool teleport = Input.GetMouseButtonDown(0);
        bool move = (p != new Point(0, 0));

        if (! (move || teleport))
            return;

        PlayerAgent pa = all.playerAgents.GetValue(me);
        PlayerData pd = all.myClient.knownPlayers.GetValue(me);
        if (!pd.connected)
            return;
        World w = all.myClient.knownWorlds.GetValue(pd.worldPos);


        if (move)
        {
			if(!w.HasPlayer(me))
				return;

            Point oldPos = w.GetPlayerPosition(me);
			Point newPos = oldPos + p;

			if (w.CheckValidMove(me, newPos) == MoveValidity.VALID)
				pa.Move(w.Info, newPos, MessageType.MOVE);
            else if (w.CheckValidMove(me, newPos) == MoveValidity.BOUNDARY)
            	pa.Move(w.Info, newPos, MessageType.REALM_MOVE);
        }
        else if (teleport)
        {
			Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            Plane xy = new Plane(Vector3.forward, new Vector3(0, 0, .5f));
            float distance;
            xy.Raycast(ray, out distance);
            var tile = ray.GetPoint(distance) + new Vector3(0, 0, 0);
            Point pos = new Point(Convert.ToInt32(tile.x), Convert.ToInt32(tile.y));
			pos = GetPositionAtMap(w, pos);
            Log.LogWriteLine("Teleporting to {0}", pos);
            pa.Move(w.Info, pos, MessageType.TELEPORT_MOVE);
        }
    }
	
	// Update is called once per frame
	void Update () {

        /*
        if (!gameStarted)
        {
            lock(all.sync.syncLock)
            {
                if (Input.GetKeyDown(KeyCode.G))
				{

					if(! all.gameAssignments.GetAllRoles().validator.Any ())
					{
						Guid valId = Guid.NewGuid();

						Role r = new Role();
						r.validator.Add(valId);
						all.AddMyRole(r);

						Log.LogWriteLine("Validator {0}", valId);
					}

					all.GenerateGame();
				}
                if(all.game == null)
                    return;
                gameStarted = true;
                StartGame();
                Debug.Log("Game started");
            }
        }*/

        lock (all.sync.syncLock)
        {
            ProcessMovement();

            while(bufferedActions.Any())
                bufferedActions.Dequeue().Invoke();

            if(Input.GetKeyDown(KeyCode.Alpha1))
            {
                if (WorldDraw.continuousBackground == true)
                {
                    WorldDraw.continuousBackground = false;
                    foreach (var w in worlds)
                        w.Value.MessBackground();
                }
            }

            //camera.transform.rotation *= Quaternion.AngleAxis(Time.deltaTime * 1, Vector3.forward);
        }
	}
}