using UnityEngine;

/// <summary>
/// Enemy with a simple health pool. TakeDamage is the kill interface the
/// bridge's bullets drive (T10); the object dies when health hits zero.
/// </summary>
public class Enemy : MonoBehaviour
{
    public int maxHealth = 3;

    public int Health { get; private set; }

    void Awake()
    {
        Health = maxHealth;
    }

    public void TakeDamage(int amount)
    {
        Health -= amount;
        if (Health <= 0)
            Destroy(gameObject);
    }
}
