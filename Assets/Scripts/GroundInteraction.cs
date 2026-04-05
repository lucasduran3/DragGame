using UnityEngine;

/// <summary>
/// GroundInteraction — piernas inertes con rozamiento y pelvis dinámica.
/// Colocar en el mismo GameObject raíz.
/// </summary>
public class GroundInteraction : MonoBehaviour
{
    [Header("Huesos de los pies")]
    public Transform footFront;
    public Transform footBack;

    [Header("Hueso Pelvis")]
    public Transform pelvis;

    [Header("Física")]
    public AlienPhysicsBody physicsBody;

    [Header("Rozamiento de piernas")]
    public float legFrictionForce = 1.8f;
    public float maxLegFriction = 5f;

    [Header("Detección de suelo bajo los pies")]
    public float footRaycastDist = 0.4f;
    public LayerMask groundLayer;

    [Header("Inclinación de pelvis")]
    public float pelvisTiltScale = 18f;
    public float maxPelvisTilt = 28f;
    [Range(2f, 15f)]
    public float pelvisTiltSmoothing = 6f;

    // ── Privadas ───────────────────────────────────────────────────────────
    private float currentPelvisTilt = 0f;
    private float pelvisTiltVelocity = 0f;
    private bool frontGrounded;
    private bool backGrounded;

    void FixedUpdate()
    {
        CheckFeet();
        ApplyLegFriction();
    }

    void LateUpdate()
    {
        UpdatePelvisTilt();
    }

    void CheckFeet()
    {
        frontGrounded = FootOnGround(footFront);
        backGrounded = FootOnGround(footBack);
    }

    bool FootOnGround(Transform foot)
    {
        if (foot == null) return false;
        return Physics2D.Raycast(
            foot.position, Vector2.down, footRaycastDist, groundLayer).collider != null;
    }

    void ApplyLegFriction()
    {
        if (physicsBody == null || !physicsBody.IsGrounded) return;
        Vector2 vel = physicsBody.Velocity;
        if (Mathf.Abs(vel.x) < 0.05f) return;

        int feet = (frontGrounded ? 1 : 0) + (backGrounded ? 1 : 0);
        if (feet == 0) return;

        float mag = Mathf.Min(legFrictionForce * feet, maxLegFriction);
        Vector2 fric = new Vector2(-Mathf.Sign(vel.x) * mag, 0f);
        physicsBody.ApplyGripForce(fric, physicsBody.transform.position);
    }

    void UpdatePelvisTilt()
    {
        if (pelvis == null) return;
        float target = 0f;
        if (footFront != null && footBack != null)
        {
            float h = footFront.position.y - footBack.position.y;
            target = Mathf.Clamp(h * pelvisTiltScale, -maxPelvisTilt, maxPelvisTilt);
        }
        currentPelvisTilt = Mathf.SmoothDamp(
            currentPelvisTilt, target, ref pelvisTiltVelocity,
            1f / pelvisTiltSmoothing);

        Vector3 e = pelvis.localEulerAngles;
        e.z = currentPelvisTilt;
        pelvis.localEulerAngles = e;
    }

    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;
        DrawFootRay(footFront, frontGrounded);
        DrawFootRay(footBack, backGrounded);
    }

    void DrawFootRay(Transform foot, bool grounded)
    {
        if (foot == null) return;
        Gizmos.color = grounded ? Color.green : Color.red;
        Gizmos.DrawLine(foot.position, foot.position + Vector3.down * footRaycastDist);
    }
}