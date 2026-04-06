// ArmLocomotionController.cs � replace full file
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class ArmLocomotionController : MonoBehaviour
{
    [Header("Arm References")]
    public ArmController frontArm;
    public ArmController backArm;

    [Header("Physics")]
    public Rigidbody2D rb;

    [Header("Velocity Clamp")]
    [SerializeField] private float maxVelocity = 10f;

    [Header("Damping")]
    [SerializeField] private float linearDamping = 2.5f;
    [SerializeField] private float angularDamping = 12f;

    [Header("Rotation Stabilisation")]
    [SerializeField] private float targetAngle = 0f;
    [SerializeField] private float rotationSpring = 80f;
    [SerializeField] private float rotationDamper = 18f;
    [SerializeField] private float maxCorrectionTorque = 20f;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        rb.gravityScale = 1f;
        rb.linearDamping = linearDamping;
        rb.angularDamping = angularDamping;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.constraints = RigidbodyConstraints2D.None;
    }

    private void FixedUpdate()
    {
        ClampVelocity();
        ApplyRotationSpring();
    }

    private void ClampVelocity()
    {
        if (rb.linearVelocity.magnitude > maxVelocity)
            rb.linearVelocity = rb.linearVelocity.normalized * maxVelocity;
    }

    private void ApplyRotationSpring()
    {
        float angleDelta = Mathf.DeltaAngle(rb.rotation, targetAngle);
        float torque = (angleDelta * rotationSpring) - (rb.angularVelocity * rotationDamper);
        torque = Mathf.Clamp(torque, -maxCorrectionTorque, maxCorrectionTorque);
        rb.AddTorque(torque, ForceMode2D.Force);
    }
}