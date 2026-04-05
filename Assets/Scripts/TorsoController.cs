using UnityEngine;

/// <summary>
/// TorsoController v4 — inclinación procedural con torque por punto de apoyo.
///
/// NUEVO EN v4:
///   - ReceiveEffort acepta ahora el vector de fuerza completo + gripOffset
///     (posición relativa del punto de apoyo respecto al hombro).
///   - El torque se calcula como el producto cruzado 2D de gripOffset × force,
///     lo que produce una rotación coherente con la física real:
///       · apoyo adelante + empuje hacia atrás → torso se inclina hacia adelante
///       · apoyo abajo + empuje hacia arriba → torso se erguye
///   - Se mantiene el balanceo de reposo (idle sway) y el damping suave.
///   - Se añade un límite de tasa de cambio (maxTiltSpeed) para evitar
///     rotaciones instantáneas antiestéticas.
/// </summary>
public class TorsoController : MonoBehaviour
{
    [Header("Hueso Torso")]
    public Transform torsoTransform;

    [Header("Referencia al cuerpo físico")]
    public AlienPhysicsBody physicsBody;

    [Header("Torque por punto de apoyo")]
    [Tooltip("Escala el torque calculado como gripOffset × force. " +
             "Recomendado: 0.4 – 1.2.")]
    public float torqueScale = 0.7f;
    [Range(0f, 60f)]
    public float maxTorqueTilt = 40f;

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

    // Acumuladores por frame (reseteados en LateUpdate)
    private float accumulatedTorque = 0f;
    private bool anyArmGripped = false;
    private bool anyArmPushing = false;

    // ──────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Llamado por cada ArmProbe en FixedUpdate.
    /// force      = fuerza aplicada al cuerpo en ese frame.
    /// gripped    = ¿el brazo está agarrado?
    /// gripOffset = gripPoint - shoulderPos (vector 2D del brazo al apoyo).
    /// </summary>
    public void ReceiveEffort(Vector2 force, bool gripped, Vector2 gripOffset = default)
    {
        if (gripped) anyArmGripped = true;

        if (force.sqrMagnitude > 0.001f)
        {
            anyArmPushing = true;

            // Torque 2D: componente Z del producto cruzado (gripOffset × force)
            // Signo positivo → inclinación en sentido horario (en Unity 2D, negativo = derecha)
            float torque2D = gripOffset.x * force.y - gripOffset.y * force.x;
            accumulatedTorque += torque2D;
        }
    }

    // Compatibilidad con firma anterior (sin gripOffset)
    public void ReceiveEffort(Vector2 force, bool gripped)
        => ReceiveEffort(force, gripped, Vector2.zero);

    void LateUpdate()
    {
        if (torsoTransform == null) return;

        float targetTilt = ComputeTargetTilt();

        currentTiltZ = Mathf.SmoothDamp(
            currentTiltZ, targetTilt, ref tiltVelocity, smoothTime, maxTiltSpeed);

        Vector3 euler = torsoTransform.localEulerAngles;
        euler.z = currentTiltZ;
        torsoTransform.localEulerAngles = euler;

        // Reset acumuladores
        accumulatedTorque = 0f;
        anyArmGripped = false;
        anyArmPushing = false;
    }

    float ComputeTargetTilt()
    {
        float tilt = 0f;

        // 1. Torque por punto de apoyo
        float torqueTilt = Mathf.Clamp(
            accumulatedTorque * torqueScale,
            -maxTorqueTilt, maxTorqueTilt);
        tilt += torqueTilt;

        // 2. Inercia por velocidad horizontal del cuerpo
        if (physicsBody != null)
        {
            float velTilt = -physicsBody.Velocity.x * velocityTiltScale;
            tilt += Mathf.Clamp(velTilt, -maxVelocityTilt, maxVelocityTilt);
        }

        // 3. Balanceo de reposo
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