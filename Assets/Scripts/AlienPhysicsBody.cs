using UnityEngine;

/// <summary>
/// Cuerpo físico central del alien.
/// Gestiona Rigidbody2D, detección de suelo y API de fuerzas.
/// Colocar en el GameObject raíz (mismo que IK Manager 2D).
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class AlienPhysicsBody : MonoBehaviour
{
    [Header("Masa y física")]
    public float bodyMass = 4f;
    public float gravityScale = 1.8f;

    [Header("Fricción dinámica")]
    [Tooltip("Drag lineal cuando hay al menos un brazo agarrado.")]
    public float grippedLinearDrag = 4f;
    [Tooltip("Drag lineal sin agarre.")]
    public float freeLinearDrag = 0.6f;
    [Tooltip("Drag angular.")]
    public float angularDrag = 6f;

    [Header("Detección de suelo")]
    public Transform groundCheck;
    public float groundCheckRadius = 0.28f;
    public LayerMask groundLayer;

    // ── API pública ────────────────────────────────────────────────────────
    public Rigidbody2D Rb { get; private set; }
    public bool IsGrounded { get; private set; }
    public Vector2 Velocity => Rb.linearVelocity;
    public int GrippedArmCount { get; set; }

    void Awake()
    {
        Rb = GetComponent<Rigidbody2D>();
        Rb.mass = bodyMass;
        Rb.gravityScale = gravityScale;
        Rb.angularDamping = angularDrag;
        Rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        Rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
    }

    void FixedUpdate()
    {
        CheckGround();
        Rb.linearDamping = GrippedArmCount > 0 ? grippedLinearDrag : freeLinearDrag;
    }

    public void ApplyGripForce(Vector2 force, Vector2 worldPoint)
    {
        //Rb.AddForceAtPosition(force, worldPoint, ForceMode2D.Force);
        Rb.AddForce(force, ForceMode2D.Force);
    }

    void CheckGround()
    {
        if (groundCheck == null) return;
        IsGrounded = Physics2D.OverlapCircle(
            groundCheck.position, groundCheckRadius, groundLayer);
    }

    void OnDrawGizmosSelected()
    {
        if (groundCheck == null) return;
        Gizmos.color = IsGrounded ? Color.green : Color.red;
        Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius);
    }
}