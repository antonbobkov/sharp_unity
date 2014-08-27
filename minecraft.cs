using UnityEngine;
using System.Collections.Generic;

using ServerClient;

public class minecraft : MonoBehaviour {

    ActionSyncronizer sync;
    NodeHost myHost;

    Vector3 vCorner;

    bool gameStarted = false;

	GameObject[,] go;
    Dictionary<System.Guid, GameObject> players = new Dictionary<System.Guid, GameObject>();


    Vector3 GetPositionAtGrid(int x, int y)
    {
        return vCorner + new Vector3(x, y, 0);
    }

    void StartGame()
    {



        Game g = myHost.dc.game;
        int szX = g.world.GetLength(0);
        int szY = g.world.GetLength(1);
        go = new GameObject[szX, szY];

        for (int i = 0; i < szX; ++i)
        {
            for (int j = 0; j < szY; ++j)
            {
                if (g.world[i, j].solid)
                {
                    go [i, j] = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go [i, j].transform.position = GetPositionAtGrid(i, j);
                }
            }
        }

        foreach (var pair in g.players)
        {
            System.Guid id = pair.Key;
            Player p = pair.Value;

            var avatar = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            avatar.renderer.material.color = new Color(Random.Range(0F,1F),Random.Range(0F,1F),Random.Range(0F,1F));

            avatar.transform.position = GetPositionAtGrid(p.pos.x, p.pos.y);
            players.Add(id, avatar);
        }
    }

    const float cameraDistance = 50f;

	// Use this for initialization
	void Start () {
        DataCollection.log = msg => Debug.Log(msg); 
        
        DataCollection.LogWriteLine("{0}", new System.Random(12).Next());

        //throw new UnityException();

        sync = new ActionSyncronizer();
        myHost = new NodeHost(sync.GetAsDelegate());

        var light = gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 20;

        camera.isOrthoGraphic = true;

        vCorner = camera.ViewportToWorldPoint(new Vector3(.5f, .5f, cameraDistance));

   }
	
	// Update is called once per frame
	void Update () {
        if (!gameStarted)
        {
            lock(myHost.dc)
            {
                if(myHost.dc.game == null)
                    return;
                gameStarted = true;
                StartGame();
                Debug.Log("Game started");
            }
        }

        lock (myHost.dc)
        {
            foreach (var pair in myHost.dc.game.players)
            {
                System.Guid id = pair.Key;
                Player p = pair.Value;
                
                players[id].transform.position = GetPositionAtGrid(p.pos.x, p.pos.y);
            }

            camera.transform.position = players[myHost.dc.Id].transform.position;
            camera.transform.position += new Vector3(0f, 0f, -cameraDistance);
            //camera.transform.rotation *= Quaternion.AngleAxis(Time.deltaTime * 1, Vector3.forward);
        }
	}
}