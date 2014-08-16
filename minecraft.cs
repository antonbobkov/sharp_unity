using UnityEngine;
using System.Collections.Generic;

public class minecraft : MonoBehaviour {

	const int WORLD_SIZE = 100;

    int[,] grid = new int[WORLD_SIZE, WORLD_SIZE];

    readonly float TILE_SIZE = .2f;

	GameObject[,] go;

    GameObject guy;

	int x = 0, y = 0;

    Vector3 vCorner;

    Vector3 GetPositionAtGrid(int x, int y)
    {
        return vCorner + new Vector3(x, y, 0);
            //- new Vector3(WORLD_SIZE / 2f, WORLD_SIZE / 2f, 0);
    }

	// Use this for initialization
	void Start () {
        var light = gameObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 20;

        camera.isOrthoGraphic = true;

		go = new GameObject[WORLD_SIZE, WORLD_SIZE];

        vCorner = camera.ViewportToWorldPoint(new Vector3(.5f, .5f, 10));

		for (int i = 0; i < WORLD_SIZE; ++i)
        {
            for (int j = 0; j < WORLD_SIZE; ++j)
            {
                if(Random.Range(0f, 1f) < .2)
                    grid[i, j] = 1;

                if (grid [i, j] != 0)
                {
                    go [i, j] = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go [i, j].transform.position = GetPositionAtGrid(i, j);
                }
            }
        }
        guy = GameObject.CreatePrimitive(PrimitiveType.Sphere);

    }
	
	// Update is called once per frame
	void Update () {
        int old_x = x;
        int old_y = y;

        if (Input.GetKey(KeyCode.UpArrow))
            ++y;
        if (Input.GetKey(KeyCode.DownArrow))
            --y;
        if (Input.GetKey(KeyCode.RightArrow))
            ++x;
        if (Input.GetKey(KeyCode.LeftArrow))
            --x;
        y = (y + WORLD_SIZE)%WORLD_SIZE;
        x = (x + WORLD_SIZE)%WORLD_SIZE;

        if (go [x, y] != null)
        {
            x = old_x;
            y = old_y;
        }

        guy.transform.position = GetPositionAtGrid(x, y);
        camera.transform.position = guy.transform.position;
        camera.transform.position += new Vector3(0f, 0f, -10f);
        //camera.transform.rotation *= Quaternion.AngleAxis(Time.deltaTime * 1, Vector3.forward);
	}
}