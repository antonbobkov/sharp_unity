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

public class minecraft : MonoBehaviour {

    Aggregate all;

    Vector3 vCorner;

    bool gameStarted = false;

	GameObject[,] go;
    Dictionary<System.Guid, GameObject> players = new Dictionary<System.Guid, GameObject>();

    Guid me;

    Queue<Action> bufferedActions = new Queue<Action>();

    Vector3 GetPositionAtGrid(int x, int y)
    {
        return vCorner + new Vector3(x, y, 0);
    }


    const float cameraDistance = 20f;

	// Use this for initialization
	void Start () {
        Log.log = msg => Debug.Log(msg); 

        all = new Aggregate();

        all.sync.Add(() =>
        {
            me = Guid.NewGuid();

			Role r = new Role();
			r.player.Add(me);
			all.AddMyRole(r);
            //all.myRole.validator.Add(Guid.NewGuid());
            Log.LogWriteLine("Player {0}", me);

            // mesh connect
            Program.MeshConnect(all);
        });

        var light = gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = cameraDistance*1.5f;
        light.intensity = 5;

        //camera.isOrthoGraphic = true;
        camera.orthographicSize = 10;

        vCorner = new Vector3(0, 0, 0);//camera.ViewportToWorldPoint(new Vector3(.5f, .5f, 0));

        Application.runInBackground = true;
    }

    void onMove(Point pos)
    {
        GameObject obj = go [pos.x, pos.y];
        if (obj != null)
        {
            Destroy(obj);
            go [pos.x, pos.y] = null;
        }
    }

    void StartGame()
    {
		all.SetMoveHook( (players, pos) => bufferedActions.Enqueue(() => onMove(pos)));
		
		Game g = all.game;
        int szX = g.world.GetLength(0);
        int szY = g.world.GetLength(1);
        go = new GameObject[szX, szY];
        
        GameObject bck = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bck.transform.localScale = new Vector3(szX, szY, 1);
        bck.transform.position = vCorner + new Vector3(szX-1, szY-1, 1)/2;
        bck.renderer.material.color = new Color(.7f, .7f, .7f);
        
        for (int i = 0; i < szX; ++i)
        {
            for (int j = 0; j < szY; ++j)
            {
                if (g.world[i, j].solid)
                {
                    GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go [i, j] = wall;

                    wall.transform.position = GetPositionAtGrid(i, j);
                    wall.renderer.material.color = new Color(.3f, .3f, .3f);
                }
                else if(g.world[i,j].loot)
                {
                    GameObject loot = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    go [i, j] = loot;

                    loot.transform.localScale = new Vector3(.5f, .5f, 1);
                    loot.renderer.material.color = Color.blue;
                    loot.transform.position = GetPositionAtGrid(i, j);
                    loot.transform.rotation = Quaternion.AngleAxis(15f, UnityEngine.Random.onUnitSphere);
                    loot.AddComponent<RotateSlowly>();
                }
            }
        }
        
        foreach (var pair in g.players)
        {
            System.Guid id = pair.Key;
            Player p = pair.Value;
            
            var avatar = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            
            //Convert.ToInt32( id.ToByteArray()
            var r = new ServerClient.MyRandom(BitConverter.ToInt32(id.ToByteArray(), 0));
            
            avatar.renderer.material.color = new Color(
                (float) r.NextDouble(),
                (float) r.NextDouble(),
                (float) r.NextDouble());
            
            avatar.transform.position = GetPositionAtGrid(p.pos.x, p.pos.y);
            players.Add(id, avatar);
        }
    }

    void OnApplicationQuit () {
		Log.LogWriteLine ("Terminating");
		all.peers.Close();
		System.Threading.Thread.Sleep (100);
		Log.LogWriteLine (ThreadManager.Status ());
		ThreadManager.Terminate();
		//System.Threading.Thread.Sleep (100);
		//all.sync.Add(null);
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

        if (Input.GetKeyDown(KeyCode.Space))
        {
            p = p + new Point(1,1);
        }

        if (p.x != 0 || p.y != 0)
        {
            Game g = all.game;
            Player pl = g.players[me];
			Point newPos = pl.pos + p;

            if (g.CheckValidMove(pl, newPos) == MoveValidity.VALID)
            {
                //Log.LogWriteLine("Move from {0} to {1}", pl.pos, mv.pos);
				all.Move(pl, newPos);
            }

            if (Input.GetKeyDown(KeyCode.Space))
            {
                for(int i = 0; i < 10000; ++i)
					all.Move(pl, newPos);
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            Plane xy = new Plane(Vector3.forward, new Vector3(0, 0, .5f));
            float distance;
            xy.Raycast(ray, out distance);
            var tile = ray.GetPoint(distance) + new Vector3(0, 0, 0);
            Point pos = new Point(Convert.ToInt32(tile.x), Convert.ToInt32(tile.y));
            Log.LogWriteLine("Teleporting to {0}", pos);
            Player pl = all.game.players[me];
            all.gameAssignments.NodeById(all.game.worldValidator)
				.SendMessage(MessageType.VALIDATE_TELEPORT, pl.id, all.game.worldValidator, pos);
        }
    }
	
	// Update is called once per frame
	void Update () {

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
        }

        lock (all.sync.syncLock)
        {
            ProcessMovement();

            while(bufferedActions.Any())
                bufferedActions.Dequeue().Invoke();

            foreach (var pair in all.game.players)
            {
                System.Guid id = pair.Key;
                Player p = pair.Value;
                
                players[id].transform.position = GetPositionAtGrid(p.pos.x, p.pos.y);
            }

            camera.transform.position = players[me].transform.position;
            camera.transform.position += new Vector3(0f, 0f, -cameraDistance);
            //camera.transform.rotation *= Quaternion.AngleAxis(Time.deltaTime * 1, Vector3.forward);
        }
	}
}