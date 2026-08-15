using UnityEngine;

/// <summary>
/// Player movement controller. WASD/arrow keys move the player on the XZ
/// plane. `FrameCount` and `SimulateMove` exist so the bridge can verify
/// behaviour in play mode (Unity's Input cannot be driven externally).
/// </summary>
public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 5f;

    public Vector3 LastInput { get; private set; }
    public long FrameCount { get; private set; }

    void Update()
    {
        FrameCount++;
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        LastInput = new Vector3(h, 0, v);
        if (LastInput.sqrMagnitude > 0.01f)
            transform.position += LastInput.normalized * moveSpeed * Time.deltaTime;
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
