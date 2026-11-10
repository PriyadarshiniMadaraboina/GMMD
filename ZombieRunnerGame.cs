using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ZOMBIE RUNNER - FACTORY  (complete game in ONE script)
///
/// HOW TO USE:
///   1. Create a new 3D Unity project, open any scene.
///   2. Drop this file into the Assets folder.
///   3. Press Play. (No need to attach it to anything - it starts itself.)
///
/// CONTROLS:  A / D or Arrow keys = move,  SPACE = jump,  R = restart after game over
///
/// GAME MECHANICS IMPLEMENTED (use these in your project report):
///   1. Auto-run movement + lane steering + jump   (CORE MECHANIC)
///   2. Obstacles: crates (jump over) and machines (dodge)
///   3. Enemies: zombies that chase you sideways
///   4. Health system: 3 lives, a hit slows you down, brief invincibility
///   5. Collectibles: batteries give score
///   6. Power-up: shield (timed) that smashes through everything
///   7. Difficulty scaling: speed and spawn rate increase over time
///   8. Score system: distance + batteries
/// </summary>
public class ZombieRunnerGame : MonoBehaviour
{
    // ================== SETTINGS (tweak these to balance the game) ==================
    [Header("Player")]
    public float startSpeed = 8f;
    public float maxSpeed = 24f;
    public float acceleration = 0.12f;   // speed gained per second
    public float sideSpeed = 8f;
    public float jumpSpeed = 9f;
    public int maxHealth = 3;

    [Header("World")]
    public float corridorHalfWidth = 4.5f;
    public float spawnAhead = 70f;

    // ================== INTERNAL TYPES ==================
    enum Kind { Crate, Machine, Zombie, Battery, Shield }

    class Entity
    {
        public GameObject go;
        public Kind kind;
        public float halfX, halfZ, height, phase;
    }

    // ================== STATE ==================
    static ZombieRunnerGame instance;

    GameObject world;
    Transform track, player;
    Rigidbody rb;
    Renderer playerRend;
    Camera cam;

    readonly List<Entity> entities = new List<Entity>();
    readonly List<GameObject> decor = new List<GameObject>();

    float speed, shieldTimer, hitCooldown, nextSpawnZ, nextDecorZ, runTime;
    int health, batteries;
    bool gameOver, jumpQueued;

    GUIStyle bigStyle, midStyle;

    // ================== STARTUP ==================
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (instance == null)
            new GameObject("ZombieRunnerGame").AddComponent<ZombieRunnerGame>();
    }

    void Awake()
    {
        instance = this;
        Physics.gravity = new Vector3(0f, -25f, 0f);
        SetupEnvironment();
        Build();
    }

    void SetupEnvironment()
    {
        cam = Camera.main;
        if (cam == null)
        {
            GameObject c = new GameObject("Main Camera");
            c.tag = "MainCamera";
            cam = c.AddComponent<Camera>();
            c.AddComponent<AudioListener>();
        }

        Color dark = new Color(0.04f, 0.04f, 0.06f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = dark;
        cam.farClipPlane = 120f;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.30f, 0.30f, 0.36f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = dark;
        RenderSettings.fogStartDistance = 20f;
        RenderSettings.fogEndDistance = 75f;
    }

    // Builds (or rebuilds) the whole level
    void Build()
    {
        world = new GameObject("World");
        entities.Clear();
        decor.Clear();

        speed = startSpeed;
        health = maxHealth;
        batteries = 0;
        shieldTimer = 0f;
        hitCooldown = 0f;
        runTime = 0f;
        gameOver = false;
        jumpQueued = false;
        nextSpawnZ = 30f;
        nextDecorZ = 0f;

        // ---- Track: floor, walls, ceiling (follows the player so it never ends) ----
        track = new GameObject("Track").transform;
        track.SetParent(world.transform);
        float w = corridorHalfWidth;
        Prim(PrimitiveType.Cube, track, new Vector3(0, -0.5f, 50f), new Vector3(w * 2f + 3f, 1f, 200f), new Color(0.20f, 0.20f, 0.23f), true);
        Prim(PrimitiveType.Cube, track, new Vector3(-(w + 1.5f), 4f, 50f), new Vector3(1f, 8f, 200f), new Color(0.28f, 0.25f, 0.22f));
        Prim(PrimitiveType.Cube, track, new Vector3(w + 1.5f, 4f, 50f), new Vector3(1f, 8f, 200f), new Color(0.28f, 0.25f, 0.22f));
        Prim(PrimitiveType.Cube, track, new Vector3(0, 8.5f, 50f), new Vector3(w * 2f + 4f, 1f, 200f), new Color(0.12f, 0.12f, 0.14f));

        // ---- Player ----
        GameObject p = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        p.name = "Player";
        p.transform.SetParent(world.transform);
        p.transform.position = new Vector3(0, 1.05f, 0);
        playerRend = p.GetComponent<Renderer>();
        rb = p.AddComponent<Rigidbody>();
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        player = p.transform;

        // Visor so you can see which way the player faces
        Prim(PrimitiveType.Cube, player, new Vector3(0, 0.6f, 0.42f), new Vector3(0.7f, 0.25f, 0.2f), new Color(0.05f, 0.05f, 0.05f));

        // Flashlight
        GameObject lightGo = new GameObject("Flashlight");
        lightGo.transform.SetParent(player, false);
        lightGo.transform.localPosition = new Vector3(0, 2f, -1f);
        Light pl = lightGo.AddComponent<Light>();
        pl.type = LightType.Point;
        pl.range = 18f;
        pl.intensity = 2.5f;
        pl.color = new Color(1f, 0.95f, 0.8f);
    }

    void Restart()
    {
        Destroy(world);
        Build();
    }

    // ================== MAIN LOOP ==================
    void Update()
    {
        if (player == null) return;

        if (gameOver)
        {
            if (Input.GetKeyDown(KeyCode.R)) Restart();
            return;
        }

        float dt = Time.deltaTime;
        runTime += dt;

        if (Input.GetKeyDown(KeyCode.Space)) jumpQueued = true;

        // Difficulty scaling: the game keeps getting faster
        speed = Mathf.Min(maxSpeed, speed + acceleration * dt);
        if (shieldTimer > 0f) shieldTimer -= dt;
        if (hitCooldown > 0f) hitCooldown -= dt;

        track.position = new Vector3(0, 0, player.position.z);

        SpawnAhead();
        UpdateEntities(dt);
        CleanupDecor();
        UpdatePlayerColor();
    }

    void FixedUpdate()
    {
        if (player == null || gameOver) return;

        Vector3 v = GetVel();
        float h = Input.GetAxisRaw("Horizontal");
        v.x = h * sideSpeed;
        v.z = speed;

        if (jumpQueued && Grounded()) v.y = jumpSpeed;
        jumpQueued = false;

        // keep the player inside the corridor
        float limit = corridorHalfWidth - 0.6f;
        Vector3 pos = rb.position;
        if ((pos.x > limit && v.x > 0f) || (pos.x < -limit && v.x < 0f)) v.x = 0f;

        SetVel(v);
    }

    void LateUpdate()
    {
        if (player == null) return;
        Vector3 pp = player.position;
        cam.transform.position = new Vector3(pp.x * 0.6f, 4.5f + pp.y * 0.3f, pp.z - 8f);
        cam.transform.LookAt(pp + new Vector3(0f, 1.2f, 8f));
    }

    bool Grounded()
    {
        return Physics.Raycast(rb.position, Vector3.down, 1.15f);
    }

    Vector3 GetVel()
    {
#if UNITY_6000_0_OR_NEWER
        return rb.linearVelocity;
#else
        return rb.velocity;
#endif
    }

    void SetVel(Vector3 v)
    {
#if UNITY_6000_0_OR_NEWER
        rb.linearVelocity = v;
#else
        rb.velocity = v;
#endif
    }

    // ================== SPAWNING ==================
    void SpawnAhead()
    {
        float diff = Mathf.InverseLerp(startSpeed, maxSpeed, speed);

        while (nextSpawnZ < player.position.z + spawnAhead)
        {
            SpawnRow(nextSpawnZ, diff);
            float timeGap = Mathf.Lerp(1.3f, 0.6f, diff);   // rows get closer together over time
            nextSpawnZ += speed * timeGap;
        }

        while (nextDecorZ < player.position.z + spawnAhead)
        {
            SpawnDecor(nextDecorZ);
            nextDecorZ += 10f;
        }
    }

    float LaneX()
    {
        float m = corridorHalfWidth - 1.5f;
        return Random.Range(-m, m);
    }

    void SpawnRow(float z, float diff)
    {
        float r = Random.value;
        float x = LaneX();

        if (r < 0.25f)
        {
            AddCrate(x, z);
        }
        else if (r < 0.35f)
        {
            AddCrate(x, z);
            float x2 = x > 0f ? x - 3.5f : x + 3.5f;
            AddCrate(x2, z);
        }
        else if (r < 0.45f)
        {
            AddMachine(x, z);
        }
        else if (r < 0.60f + diff * 0.20f)
        {
            AddZombie(x, z);
        }
        else if (r < 0.95f)
        {
            for (int i = 0; i < 5; i++) AddBattery(x, z + i * 2f);
        }
        else
        {
            AddShield(x, z);
        }
    }

    Entity NewEntity(Kind k, float x, float z, float halfX, float halfZ, float height)
    {
        GameObject go = new GameObject(k.ToString());
        go.transform.SetParent(world.transform);
        go.transform.position = new Vector3(x, 0f, z);
        Entity e = new Entity { go = go, kind = k, halfX = halfX, halfZ = halfZ, height = height, phase = Random.value * 10f };
        entities.Add(e);
        return e;
    }

    void AddCrate(float x, float z)
    {
        Entity e = NewEntity(Kind.Crate, x, z, 0.9f, 0.9f, 1.2f);
        Transform t = e.go.transform;
        Prim(PrimitiveType.Cube, t, new Vector3(0, 0.6f, 0), new Vector3(1.8f, 1.2f, 1.8f), new Color(0.55f, 0.36f, 0.18f));
        Prim(PrimitiveType.Cube, t, new Vector3(0, 1.21f, 0), new Vector3(1.9f, 0.08f, 1.9f), new Color(0.85f, 0.7f, 0.1f));
    }

    void AddMachine(float x, float z)
    {
        Entity e = NewEntity(Kind.Machine, x, z, 1.5f, 1.0f, 3f);
        Transform t = e.go.transform;
        Prim(PrimitiveType.Cube, t, new Vector3(0, 1.5f, 0), new Vector3(3f, 3f, 2f), new Color(0.35f, 0.38f, 0.42f));
        Prim(PrimitiveType.Cube, t, new Vector3(0, 3.1f, 0), new Vector3(0.5f, 0.35f, 0.5f), new Color(0.9f, 0.1f, 0.1f));
        Prim(PrimitiveType.Cube, t, new Vector3(0, 0.3f, -1.01f), new Vector3(3.1f, 0.4f, 0.05f), new Color(0.9f, 0.75f, 0.1f));
    }

    void AddZombie(float x, float z)
    {
        Entity e = NewEntity(Kind.Zombie, x, z, 0.6f, 0.5f, 2.2f);
        Transform t = e.go.transform;
        Color skin = new Color(0.35f, 0.55f, 0.30f);
        Prim(PrimitiveType.Capsule, t, new Vector3(0, 1f, 0), new Vector3(0.9f, 1f, 0.9f), new Color(0.25f, 0.35f, 0.22f));   // body
        Prim(PrimitiveType.Sphere, t, new Vector3(0, 2.05f, 0), new Vector3(0.65f, 0.65f, 0.65f), skin);                   // head
        Prim(PrimitiveType.Cube, t, new Vector3(-0.5f, 1.5f, -0.5f), new Vector3(0.2f, 0.2f, 0.9f), skin);                 // arms reach toward the player
        Prim(PrimitiveType.Cube, t, new Vector3(0.5f, 1.5f, -0.5f), new Vector3(0.2f, 0.2f, 0.9f), skin);
        Prim(PrimitiveType.Cube, t, new Vector3(-0.14f, 2.1f, -0.28f), new Vector3(0.1f, 0.08f, 0.05f), Color.red);        // eyes
        Prim(PrimitiveType.Cube, t, new Vector3(0.14f, 2.1f, -0.28f), new Vector3(0.1f, 0.08f, 0.05f), Color.red);
    }

    void AddBattery(float x, float z)
    {
        Entity e = NewEntity(Kind.Battery, x, z, 0.6f, 0.6f, 2.5f);
        Transform t = e.go.transform;
        Prim(PrimitiveType.Cylinder, t, new Vector3(0, 1.2f, 0), new Vector3(0.35f, 0.4f, 0.35f), new Color(1f, 0.85f, 0.1f));
        Prim(PrimitiveType.Cube, t, new Vector3(0, 1.65f, 0), new Vector3(0.2f, 0.1f, 0.2f), new Color(0.8f, 0.8f, 0.8f));
    }

    void AddShield(float x, float z)
    {
        Entity e = NewEntity(Kind.Shield, x, z, 0.8f, 0.8f, 2.5f);
        Prim(PrimitiveType.Sphere, e.go.transform, new Vector3(0, 1.3f, 0), new Vector3(0.9f, 0.9f, 0.9f), new Color(0.1f, 0.9f, 1f));
    }

    // Background decoration: hazard stripes, pillars, ceiling lamps
    void SpawnDecor(float z)
    {
        float w = corridorHalfWidth;
        GameObject d = new GameObject("Decor");
        d.transform.SetParent(world.transform);
        d.transform.position = new Vector3(0, 0, z);

        Prim(PrimitiveType.Cube, d.transform, new Vector3(0, 0.02f, 0), new Vector3(w * 2f, 0.04f, 0.8f), new Color(0.85f, 0.7f, 0.1f));
        Prim(PrimitiveType.Cube, d.transform, new Vector3(-(w + 0.4f), 4f, 0), new Vector3(0.6f, 8f, 0.6f), new Color(0.15f, 0.15f, 0.17f));
        Prim(PrimitiveType.Cube, d.transform, new Vector3(w + 0.4f, 4f, 0), new Vector3(0.6f, 8f, 0.6f), new Color(0.15f, 0.15f, 0.17f));
        Prim(PrimitiveType.Cube, d.transform, new Vector3(0, 7.8f, 0), new Vector3(3f, 0.2f, 1f), new Color(1f, 0.95f, 0.7f));
        decor.Add(d);
    }

    void CleanupDecor()
    {
        float limit = player.position.z - 15f;
        for (int i = decor.Count - 1; i >= 0; i--)
        {
            if (decor[i].transform.position.z < limit)
            {
                Destroy(decor[i]);
                decor.RemoveAt(i);
            }
        }
    }

    // ================== ENTITIES: movement + collisions ==================
    void UpdateEntities(float dt)
    {
        Vector3 pp = player.position;
        float diff = Mathf.InverseLerp(startSpeed, maxSpeed, speed);

        for (int i = entities.Count - 1; i >= 0; i--)
        {
            Entity e = entities[i];
            Transform t = e.go.transform;
            Vector3 p = t.position;

            if (e.kind == Kind.Zombie)
            {
                // zombies wake up when you get close, walk toward you and drift into your lane
                if (p.z - pp.z < 35f)
                {
                    p.z -= (2.5f + diff * 3f) * dt;
                    p.x = Mathf.MoveTowards(p.x, pp.x, (1.5f + diff * 1.5f) * dt);
                }
                p.y = Mathf.Abs(Mathf.Sin(Time.time * 7f + e.phase)) * 0.15f;   // shambling bob
                t.position = p;
            }
            else if (e.kind == Kind.Battery || e.kind == Kind.Shield)
            {
                t.Rotate(0f, 180f * dt, 0f);
            }

            // behind the player -> remove
            if (p.z < pp.z - 15f)
            {
                Destroy(e.go);
                entities.RemoveAt(i);
                continue;
            }

            // collision test (box overlap in X/Z, plus height so crates can be jumped over)
            bool overlap = Mathf.Abs(p.x - pp.x) < e.halfX + 0.45f &&
                           Mathf.Abs(p.z - pp.z) < e.halfZ + 0.45f;
            if (!overlap) continue;

            float feet = pp.y - 1f;
            if (feet >= e.height) continue;   // jumped over it

            switch (e.kind)
            {
                case Kind.Battery:
                    batteries++;
                    break;
                case Kind.Shield:
                    shieldTimer = 6f;
                    break;
                default:    // crate, machine, zombie
                    TakeHit();
                    break;
            }

            Destroy(e.go);
            entities.RemoveAt(i);
        }
    }

    void TakeHit()
    {
        if (shieldTimer > 0f || hitCooldown > 0f) return;   // shield / invincibility frames

        health--;
        hitCooldown = 1f;
        speed = Mathf.Max(startSpeed, speed * 0.7f);         // penalty: you lose speed

        if (health <= 0)
        {
            health = 0;
            gameOver = true;
            rb.isKinematic = true;
        }
    }

    void UpdatePlayerColor()
    {
        Color c = new Color(0.2f, 0.6f, 1f);
        if (shieldTimer > 0f) c = Color.cyan;
        else if (hitCooldown > 0f && Mathf.Repeat(Time.time * 10f, 1f) < 0.5f) c = Color.red;
        playerRend.material.color = c;
    }

    // ================== HELPERS ==================
    GameObject Prim(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Color color, bool keepCollider = false)
    {
        GameObject g = GameObject.CreatePrimitive(type);
        if (!keepCollider)
        {
            Collider col = g.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }
        g.transform.SetParent(parent, false);
        g.transform.localPosition = localPos;
        g.transform.localScale = scale;
        g.GetComponent<Renderer>().material.color = color;
        return g;
    }

    int Score()
    {
        return Mathf.FloorToInt(Mathf.Max(0f, player.position.z)) + batteries * 25;
    }

    // ================== HUD ==================
    void OnGUI()
    {
        if (player == null) return;

        if (bigStyle == null)
        {
            bigStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 44,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            midStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
        }

        GUI.Label(new Rect(20, 12, 400, 34), "SCORE: " + Score(), midStyle);
        GUI.Label(new Rect(20, 44, 120, 30), "HEALTH", midStyle);
        DrawBar(new Rect(140, 48, 220, 22), (float)health / maxHealth, new Color(0.9f, 0.15f, 0.15f));

        if (shieldTimer > 0f)
        {
            GUI.Label(new Rect(20, 76, 120, 30), "SHIELD", midStyle);
            DrawBar(new Rect(140, 80, 220, 22), shieldTimer / 6f, Color.cyan);
        }

        GUI.Label(new Rect(Screen.width - 260, 12, 240, 34), "SPEED: " + Mathf.RoundToInt(speed * 3.6f) + " km/h", midStyle);
        GUI.Label(new Rect(Screen.width - 260, 44, 240, 34), "BATTERIES: " + batteries, midStyle);

        if (runTime < 8f && !gameOver)
            GUI.Label(new Rect(0, Screen.height - 90, Screen.width, 50), "A / D = move     SPACE = jump over crates     dodge machines & zombies", bigStyle.fontSize > 0 ? midCentered() : midStyle);

        if (gameOver)
        {
            GUI.color = new Color(0, 0, 0, 0.65f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(0, Screen.height * 0.30f, Screen.width, 70), "YOU WERE INFECTED", bigStyle);
            GUI.Label(new Rect(0, Screen.height * 0.42f, Screen.width, 60), "Score: " + Score() + "     Batteries: " + batteries, bigStyle);
            GUI.Label(new Rect(0, Screen.height * 0.54f, Screen.width, 60), "Press R to restart", bigStyle);
        }
    }

    GUIStyle centeredMid;
    GUIStyle midCentered()
    {
        if (centeredMid == null)
        {
            centeredMid = new GUIStyle(midStyle) { alignment = TextAnchor.MiddleCenter };
        }
        return centeredMid;
    }

    void DrawBar(Rect r, float fraction, Color fill)
    {
        GUI.color = new Color(0, 0, 0, 0.6f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01(fraction), r.height - 4), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }
}
