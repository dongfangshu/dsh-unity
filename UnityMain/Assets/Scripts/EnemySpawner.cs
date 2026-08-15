using UnityEngine;

/// <summary>
/// Spawns enemies at random positions around the scene at Start.
/// SpawnedCount lets the bridge verify spawning in play mode.
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    public int spawnCount = 5;
    public float radius = 6f;

    public int SpawnedCount { get; private set; }

    void Start()
    {
        for (int i = 0; i < spawnCount; i++)
        {
            var e = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            e.name = "Enemy";
            e.AddComponent<Enemy>();
            Vector2 r = Random.insideUnitCircle * radius;
            e.transform.position = new Vector3(r.x, 0.5f, r.y);
            e.transform.SetParent(transform, true);
            SpawnedCount++;
        }
    }
}
