using UnityEngine;

/// <summary>
/// Player movement + shooting controller. WASD/arrow keys move the player on
/// the XZ plane; Space fires a bullet forwards. `FrameCount`, `SimulateMove`
/// and `Fire` exist so the bridge can verify behaviour in play mode
/// (Unity's Input cannot be driven externally).
/// </summary>
public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float fireCooldown = 0.25f;
    public Transform bulletPrefab;

    public Vector3 LastInput { get; private set; }
    public long FrameCount { get; private set; }
    public int BulletCount { get; private set; }

    float _lastFire = -99f;

    void Update()
    {
        FrameCount++;
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        LastInput = new Vector3(h, 0, v);
        if (LastInput.sqrMagnitude > 0.01f)
            transform.position += LastInput.normalized * moveSpeed * Time.deltaTime;

        if (Input.GetKeyDown(KeyCode.Space) && Time.time - _lastFire >= fireCooldown)
            Fire();
    }

    /// <summary>Spawn a bullet ahead of the player.</summary>
    public GameObject Fire()
    {
        _lastFire = Time.time;
        GameObject b;
        if (bulletPrefab != null)
        {
            b = Instantiate(bulletPrefab.gameObject, transform.position + transform.forward * 1f, transform.rotation);
        }
        else
        {
            b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.transform.localScale = new Vector3(0.2f, 0.2f, 0.4f);
            b.transform.position = transform.position + transform.forward * 1f;
            b.transform.forward = transform.forward;
        }
        b.name = "Bullet";
        b.AddComponent<Bullet>();
        BulletCount++;
        return b;
    }

    /// <summary>Apply movement exactly as Update() does for a given input
    /// vector — used by the bridge to verify movement in play mode.</summary>
    public void SimulateMove(Vector3 input)
    {
        var move = new Vector3(input.x, 0, input.z).normalized;
        if (move.sqrMagnitude > 0.01f)
            transform.position += move * moveSpeed * Time.deltaTime;
    }
}
