using UnityEngine;

/// <summary>
/// Straight-flying projectile with a lifetime. Damage application against
/// enemies is wired in the kill ticket (T10) — this file owns flight and
/// self-destruction only.
/// </summary>
public class Bullet : MonoBehaviour
{
    public float speed = 20f;
    public int damage = 1;
    public float lifetime = 3f;

    void Update()
    {
        transform.position += transform.forward * speed * Time.deltaTime;
        lifetime -= Time.deltaTime;
        if (lifetime <= 0f)
            Destroy(gameObject);
    }
}
