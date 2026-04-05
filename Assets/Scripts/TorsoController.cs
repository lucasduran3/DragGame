using UnityEngine;

/// <summary>
/// TorsoController v3 — inclinación procedural del hueso Torso.
/// Ejecuta en LateUpdate para sobreescribir después del IK Manager.
/// </summary>
public class TorsoController : MonoBehaviour
{
    [Header("Hueso Torso")]
    public Transform torsoTransform;

    [Header("Referencia al cuerpo físico")]
    public AlienPhysicsBody physicsBody;

    [Header("Inclinación por esfuerzo horizontal")]
    public float effortTiltScale = 0.8f;
    [Range(0f, 50f)]
    public float maxEffortTilt = 35f;

    [Header("Inclinación por esfuerzo vertical (levantamiento)")]
    [Tooltip("Cuando hay fuerza hacia arriba, el torso se comprime/estira hacia arriba.")]
    public float verticalTiltScale = 0.5f;
    [Range(0f, 30f)]
    public float maxVerticalTilt = 20f;

    [Header("Inclinación por velocidad del cuerpo (inercia)")]
    public float velocityTiltScale = 0.4f;
    [Range(0f, 35f)]
    public float maxVelocityTilt = 20f;

    [Header("Damping")]
    [Range(0.05f, 0.8f)]
    public float smoothTime = 0.18f;
    public float maxTiltSpeed = 120f;

    [Header("Balanceo de reposo")]
    public float idleSwayAmplitude = 2.5f;
    public float idleSwayFrequency = 0.5f;

    // ── Estado interno ─────────────────────────────────────────────────────
    private float currentTiltZ = 0f;
    private float tiltVelocity = 0f;
    private Vector2 accumulatedEffort;
    private bool anyArmGripped;

    // ──────────────────────────────────────────────────────────────────────
    public void ReceiveEffort(Vector2 effort, bool gripped)
    {
        accumulatedEffort += effort;
        if (gripped) anyArmGripped = true;
    }

    void LateUpdate()
    {
        if (torsoTransform == null) return;

        float targetTilt = ComputeTargetTilt();

        currentTiltZ = Mathf.SmoothDamp(
            currentTiltZ, targetTilt, ref tiltVelocity, smoothTime, maxTiltSpeed);

        Vector3 euler = torsoTransform.localEulerAngles;
        euler.z = currentTiltZ;
        torsoTransform.localEulerAngles = euler;

        accumulatedEffort = Vector2.zero;
        anyArmGripped = false;
    }

    float ComputeTargetTilt()
    {
        float tilt = 0f;

        // 1. Esfuerzo horizontal de los brazos
        float effortTilt = accumulatedEffort.x * effortTiltScale;
        tilt += Mathf.Clamp(effortTilt, -maxEffortTilt, maxEffortTilt);

        // 2. Esfuerzo vertical (levantarse): inclina el torso hacia arriba
        //    Cuando el alien se empuja hacia arriba, el torso se "erguye"
        //    ligeramente (inclinación negativa = hacia adelante en Unity 2D)
        float vertTilt = -accumulatedEffort.y * verticalTiltScale;
        tilt += Mathf.Clamp(vertTilt, -maxVerticalTilt, maxVerticalTilt);

        // 3. Inercia por velocidad horizontal del cuerpo
        if (physicsBody != null)
        {
            float velTilt = -physicsBody.Velocity.x * velocityTiltScale;
            tilt += Mathf.Clamp(velTilt, -maxVelocityTilt, maxVelocityTilt);
        }

        // 4. Balanceo de reposo
        bool idle = physicsBody == null ||
                    (physicsBody.Velocity.magnitude < 0.25f && !anyArmGripped);
        if (idle)
        {
            tilt += Mathf.Sin(Time.time * idleSwayFrequency * Mathf.PI * 2f)
                    * idleSwayAmplitude;
        }

        return tilt;
    }
}