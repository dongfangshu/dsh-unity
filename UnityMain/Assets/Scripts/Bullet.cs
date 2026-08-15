using UnityEngine;

/// <summary>
/// Straight-flying projectile: moves forward and damages the first enemy
/// whose horizontal disc the frame-to-frame trajectory segment intersects.
/// Segment-based hit detection (not per-frame point sampling) so low editor
/// frame rates — where a bullet can jump past a hit window in one frame —
/// never miss a kill. Lifetime self-destruction as a safety net.
/// </summary>
public class Bullet : MonoBehaviour
{
    public float speed = 20f;
    public int damage = 1;
    public float lifetime = 3f;
    public float hitRadius = 0.6f;

    Vector3 _prev;

    void Start()
    {
        _prev = transform.position;
    }

    void Update()
    {
        Vector3 before = _prev;
        transform.position += transform.forward * speed * Time.deltaTime;
        Vector3 after = transform.position;
        before.y = 0f;
        after.y = 0f;

        foreach (var e in Object.FindObjectsOfType<Enemy>())
        {
            if (e == null) continue;
            Vector3 enemy = e.transform.position;
            enemy.y = 0f;
            if (DistancePointSegment(enemy, before, after) <= hitRadius)
            {
                e.TakeDamage(damage);
                Destroy(gameObject);
                return;
            }
        }

        _prev = transform.position;
        lifetime -= Time.deltaTime;
        if (lifetime <= 0f)
            Destroy(gameObject);
    }

    /// <summary>Distance from point p to segment (a, b) on the XZ plane.</summary>
    static float DistancePointSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq <= 0.0001f) return (p - a).magnitude;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSq);
        return (p - (a + ab * t)).magnitude;
    }
}
