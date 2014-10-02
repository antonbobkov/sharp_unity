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

    public readonly Point sz;

	public GameObject background;
	public Plane<GameObject> walls;
	public Plane<GameObject> loots;

	public void MessBackground()
    {
        //background.transform.rotation = Quaternion.AngleAxis(2f, UnityEngine.Random.onUnitSphere);
        background.transform.localScale *= .99f;
    }
    public WorldDraw(World w)
	{
		sz = w.map.Size;

		walls = new Plane<GameObject>(sz);
		loots = new Plane<GameObject>(sz);
		
		foreach(Point pos in Point.Range(sz))
		{
			Tile t = w.map[pos];
			if (t.solid)
			{
				GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
				walls [pos] = wall;
				
				wall.transform.position = minecraft.GetPositionAtGrid(w, pos);
				wall.renderer.material.color = new Color(.3f, .3f, .3f);
			}
			else if(t.loot)
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
	}
	public void Purge()
	{
		foreach(Point pos in Point.Range(sz))
		{
			UnityEngine.Object.Destroy (walls[pos]);
			UnityEngine.Object.Destroy (loots[pos]);
		}

		UnityEngine.Object.Destroy(background);
	}
}

public class minecraft : MonoBehaviour {

    Aggregator all;

    //bool gameStarted = false;

    Dictionary<Guid, GameObject> players = new Dictionary<Guid, GameObject>();
	Dictionary<Point, WorldDraw> worlds = new Dictionary<Point, WorldDraw>();

	Guid me;

    Queue<Action> bufferedActions = new Queue<Action>();

    static internal Vector3 GetPositionAtGrid(World w, Point pos)
    {
        Point worldPos = new Point(w.Position.x * w.map.Size.x, w.Position.y * w.map.Size.y);
		return new Vector3(worldPos.x + pos.x, worldPos.y + pos.y, 0);
    }
	static internal Point GetPositionAtMap(World w, Point pos)
	{
        Point worldPos = new Point(w.Position.x * w.map.Size.x, w.Position.y * w.map.Size.y);
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
            me = Guid.NewGuid();
            Log.LogWriteLine("Player {0}", me);
            all.myClient.NewMyPlayer(me);
        };

        all.myClient.onMoveHook = (w, pl, mt) => bufferedActions.Enqueue(() => OnMove(w, pl, mt));
        all.myClient.onNewWorldHook = (w) => bufferedActions.Enqueue(() => OnNewWorld(w));

        all.sync.Start();

        var light = gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = cameraDistance*1.5f;
        light.intensity = 5;

        //camera.isOrthoGraphic = true;
        //camera.orthographicSize = 10;

        Application.runInBackground = true;
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

    void AddPlayer(World w, Guid player)
    {
        MyAssert.Assert(!players.ContainsKey(player));
        Point pos = w.playerPositions.GetValue(player);

        var avatar = GameObject.CreatePrimitive(PrimitiveType.Sphere);

        var r = new ServerClient.MyRandom(BitConverter.ToInt32(player.ToByteArray(), 0));

        avatar.renderer.material.color = new Color(
            (float)r.NextDouble(),
            (float)r.NextDouble(),
            (float)r.NextDouble());

        avatar.transform.position = GetPositionAtGrid(w, pos);
        players.Add(player, avatar);
    }

    void AddNewPlayers(World w)
    {
        foreach (Guid id in w.playerPositions.Keys)
        {
            if (!players.ContainsKey(id))
                AddPlayer(w, id);
        }
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
    }

    void OnMove(World w, PlayerInfo player, MoveType mv)
	{
        if (mv == MoveType.LEAVE)
            return;

        Point pos = w.playerPositions.GetValue(player.id);

        if (mv == MoveType.JOIN && player.id == me)
            UpdateWorlds();


        if (!worlds.ContainsKey(w.Position))
        {
            if (players.ContainsKey(player.id))
            {
                Destroy(players[player.id]);
                players.Remove(player.id);
            }
            return;
        }

        if (!players.ContainsKey(player.id))
            AddPlayer(w, player.id);

        WorldDraw wd = worlds.GetValue(w.Position);
		

		GameObject obj = wd.loots[pos];
		if (obj != null)
		{
			Destroy(obj);
			wd.loots[pos] = null;
		}

        GameObject movedPlayer = players.GetValue(player.id);
		movedPlayer.transform.position = GetPositionAtGrid(w, pos);

        if (player.id == me)
		{
			camera.transform.position = movedPlayer.transform.position;
			camera.transform.position += new Vector3(0f, 0f, -cameraDistance);
		}
	}

    /*
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

        if (Input.GetKeyDown(KeyCode.Space))
        {
            p = p + new Point(1,1);
        }

        if (p.x != 0 || p.y != 0)
        {
            Game g = all.game;
            Player pl = g.players[me];
			World w = g.GetPlayerWorld(pl.id);
			Point oldPos = w.playerPositions.GetValue(pl.id);
			Point newPos = oldPos + p;

			if (w.CheckValidMove(pl.id, newPos) == MoveValidity.VALID)
				all.Move(pl, newPos, MessageType.VALIDATE_MOVE);
			else if((w.CheckValidMove(pl.id, newPos) & ~MoveValidity.BOUNDARY) == MoveValidity.VALID)
				all.Move(pl, newPos, MessageType.VALIDATE_REALM_MOVE);



            if (Input.GetKeyDown(KeyCode.Space))
            {
                for(int i = 0; i < 10000; ++i)
					all.Move(pl, newPos, MessageType.VALIDATE_MOVE);
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
			Game g = all.game;
			Player pl = g.players[me];
			World w = g.GetPlayerWorld(pl.id);

			Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            Plane xy = new Plane(Vector3.forward, new Vector3(0, 0, .5f));
            float distance;
            xy.Raycast(ray, out distance);
            var tile = ray.GetPoint(distance) + new Vector3(0, 0, 0);
            Point pos = new Point(Convert.ToInt32(tile.x), Convert.ToInt32(tile.y));
			pos = GetPositionAtMap(w, pos);
            Log.LogWriteLine("Teleporting to {0}", pos);
			all.Move(pl, pos, MessageType.VALIDATE_TELEPORT);
        }
    }
    */
	
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
            //ProcessMovement();

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