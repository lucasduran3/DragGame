using UnityEngine;

/// <summary>
/// ArmProbe v5 — sistema de compresión / extensión.
///
/// MODELO ANTERIOR (v4): la fuerza venía del delta del mouse → irreal.
/// MODELO NUEVO (v5):    la fuerza viene del cambio de longitud del brazo.
///
/// Ciclo de un empuje:
///   1. ALCANZAR    — el IK target se posiciona en la superficie detectada.
///   2. AGARRAR     — el jugador presiona el botón; el target se clava en el mundo.
///   3. COMPRIMIR   — el cuerpo se acerca a la mano fija → el brazo se flexiona
///                    → se acumula energía proporcional a la compresión.
///   4. EXTENDER    — el cuerpo se aleja de la mano → el brazo se extiende
///                    → la energía acumulada se libera como impulso.
///   5. SOLTAR      — el jugador suelta el botón; el brazo queda libre.
///
/// Condiciones que BLOQUEAN la fuerza:
///   - Sin contacto con superficie.
///   - Brazo completamente extendido (ratio >= extensionLockRatio) sin compresión previa.
///   - Energía acumulada = 0 (no hubo fase de compresión).
/// </summary>
public class ArmProbe : MonoBehaviour
{
    // ── Identificación ─────────────────────────────────────────────────────
    [Header("Identificación")]
    public string armName = "Arm";

    // ── Referencias ────────────────────────────────────────────────────────
    [Header("Referencias")]
    public Transform shoulder;
    public Transform ikTarget;
    public AlienPhysicsBody physicsBody;
    public TorsoController torsoController;

    // ── Input ──────────────────────────────────────────────────────────────
    [Header("Input")]
    [Tooltip("0 = botón izquierdo, 1 = derecho.")]
    public int mouseButton = 0;

    // ── Detección de superficie ────────────────────────────────────────────
    [Header("Detección de superficie")]
    public float probeRadius = 0.08f;
    public float maxReach = 2.4f;
    public float minReach = 0.3f;
    public LayerMask surfaceLayer;

    [Tooltip("Offset de penetración en la superficie. Brazo frontal: 0. Trasero: 0.15-0.3.")]
    [Range(0f, 0.5f)]
    public float surfacePenetrationOffset = 0f;

    [Header("Suavizado del IK target")]
    [Range(4f, 30f)]
    public float ikFollowSpeed = 14f;

    // ── Modelo de compresión / extensión ───────────────────────────────────
    [Header("Compresión y extensión")]

    [Tooltip(
        "Fracción de maxReach a partir de la cual el brazo se considera 'en reposo'. " +
        "Por debajo → compresión. Por encima → extensión.")]
    [Range(0.3f, 0.9f)]
    public float restLengthRatio = 0.65f;

    [Tooltip(
        "Si el ratio longitud/maxReach supera este valor y no hay energía acumulada, " +
        "el brazo está demasiado extendido para generar fuerza. " +
        "Recomiendado: 0.90-0.95.")]
    [Range(0.7f, 1.0f)]
    public float extensionLockRatio = 0.92f;

    [Tooltip("Cuánta energía se acumula por unidad de compresión (distancia en unidades Unity).")]
    public float compressionGain = 8f;

    [Tooltip("Energía máxima acumulable en un solo agarre.")]
    public float maxStoredEnergy = 25f;

    [Tooltip(
        "Fracción de la energía almacenada que se convierte en fuerza por frame durante " +
        "la extensión. Valores altos = impulso rápido y brusco. Bajos = suave y sostenido.")]
    [Range(0.05f, 0.6f)]
    public float energyReleaseRate = 0.25f;

    [Tooltip("Multiplicador final sobre la fuerza liberada.")]
    public float forceMultiplier = 1f;

    [Tooltip(
        "Fracción de la fuerza total que se aplica en la dirección normal a la superficie " +
        "(levantamiento vertical). 0 = solo tangente. 0.5 = mezcla. 1 = solo perpendicular.")]
    [Range(0f, 1f)]
    public float verticalForceFraction = 0.5f;

    [Tooltip("Boost adicional sobre la componente normal (para que sea más fácil levantarse).")]
    public float verticalForceBoost = 1.3f;

    [Tooltip("Energía mínima para poder liberar fuerza (evita microfuerzas por ruido).")]
    public float minEnergyToRelease = 0.5f;

    // ── Feedback al torso ──────────────────────────────────────────────────
    [Header("Feedback al torso")]
    [Range(0f, 2f)]
    public float effortWeight = 1f;

    // ── Estado público ─────────────────────────────────────────────────────
    public bool IsGripped { get; private set; }
    public float StoredEnergy { get; private set; }
    public float CurrentArmRatio { get; private set; }  // 0=colapsado, 1=extendido
    public ArmPhase CurrentPhase { get; private set; }

    public enum ArmPhase { Free, Reaching, Compressing, Extending, Locked }

    // ── Privadas ───────────────────────────────────────────────────────────
    private Vector2 gripPoint;
    private Vector2 gripNormal;
    private Vector3 smoothedTargetPos;
    private float prevArmLength;
    private Camera mainCam;

    private bool buttonHeld;
    private Vector3 currentMouseWorld;

    // ──────────────────────────────────────────────────────────────────────
    void Start()
    {
        mainCam = Camera.main;
        if (ikTarget != null)
            smoothedTargetPos = ikTarget.position;
        prevArmLength = maxReach * restLengthRatio;
    }

    void Update()
    {
        buttonHeld = Input.GetMouseButton(mouseButton);
        currentMouseWorld = GetMouseWorldPos();
    }

    void FixedUpdate()
    {
        if (shoulder == null || ikTarget == null || physicsBody == null) return;


        // ── Dirección del probe ────────────────────────────────────────────
        Vector2 shoulderPos = shoulder.position;
        Vector3 mouseWorld = GetMouseWorldPos();
        Vector2 toMouse = (Vector2)mouseWorld - shoulderPos;
        float dist = Mathf.Clamp(toMouse.magnitude, minReach, maxReach);
        Vector2 probeDir = toMouse.sqrMagnitude > 0.0001f
                                ? toMouse.normalized
                                : Vector2.right;

        // ── CircleCast ────────────────────────────────────────────────────
        RaycastHit2D hit = Physics2D.CircleCast(
            shoulderPos, probeRadius, probeDir, dist, surfaceLayer);
        bool surfaceHit = hit.collider != null;

        // ── Longitud actual del brazo ──────────────────────────────────────
        float currentArmLength = Vector2.Distance(shoulderPos, (Vector2)ikTarget.position);
        CurrentArmRatio = currentArmLength / maxReach;

        // ── Lógica de agarre ───────────────────────────────────────────────
        if (!IsGripped)
        {
            StoredEnergy = 0f;  // Reset al soltar
            if (buttonHeld && surfaceHit)
            {
                IsGripped = true;
                gripPoint = hit.point;
                gripNormal = hit.normal;
                prevArmLength = currentArmLength;

                Vector2 pen = hit.point - hit.normal * surfacePenetrationOffset;
                smoothedTargetPos = new Vector3(pen.x, pen.y, ikTarget.position.z);
            }
        }
        else
        {
            float distToGrip = Vector2.Distance(shoulderPos, gripPoint);
            if (!buttonHeld || distToGrip > maxReach * 1.15f)
            {
                IsGripped = false;
                StoredEnergy = 0f;
            }
        }

        // ── Posición del IK target ─────────────────────────────────────────
        if (!IsGripped)
        {
            Vector3 desired;
            if (surfaceHit)
            {
                Vector2 tp = hit.point - hit.normal * surfacePenetrationOffset;
                desired = new Vector3(tp.x, tp.y, ikTarget.position.z);
            }
            else
            {
                Vector2 fp = shoulderPos + probeDir * dist;
                desired = new Vector3(fp.x, fp.y, ikTarget.position.z);
            }
            smoothedTargetPos = Vector3.Lerp(
                smoothedTargetPos, desired, Time.deltaTime * ikFollowSpeed);
            CurrentPhase = surfaceHit ? ArmPhase.Reaching : ArmPhase.Free;
        }
        // Si está agarrado, el target no se mueve.

        ikTarget.position = smoothedTargetPos;

        // ── Motor de compresión / extensión ───────────────────────────────
        Vector2 appliedForce = Vector2.zero;

        if (IsGripped)
        {
            float lengthDelta = currentArmLength - prevArmLength;
            // lengthDelta < 0 → el brazo se acortó (el cuerpo se acercó → compresión)
            // lengthDelta > 0 → el brazo se alargó (el cuerpo se alejó → extensión)

            float restLength = maxReach * restLengthRatio;

            if (lengthDelta < 0f)
            {
                Debug.Log("Acumulando energia comprimida");
                // COMPRESIÓN: acumular energía proporcional al acortamiento
                float compression = -lengthDelta;  // positivo
                StoredEnergy = Mathf.Min(
                    StoredEnergy + compression * compressionGain,
                    maxStoredEnergy);
                CurrentPhase = ArmPhase.Compressing;
            }
            else if (lengthDelta > 0f && StoredEnergy > minEnergyToRelease)
            {
                Debug.Log("Extendiendo");
                // EXTENSIÓN con energía acumulada: liberar impulso
                // Se libera una fracción de la energía por frame → curva suave
                float energyToRelease = StoredEnergy * energyReleaseRate;
                StoredEnergy -= energyToRelease;

                // Dirección de la fuerza: mezcla tangente + normal
                appliedForce = ComputeDirectionalForce(energyToRelease);
                physicsBody.ApplyGripForce(appliedForce, gripPoint);
                CurrentPhase = ArmPhase.Extending;
            }
            else if (CurrentArmRatio >= extensionLockRatio && StoredEnergy <= minEnergyToRelease)
            {
                Debug.Log("Brazo extendido sin compresion");
                // BLOQUEADO: brazo extendido sin compresión previa → sin fuerza
                CurrentPhase = ArmPhase.Locked;
            }
            else
            {
                CurrentPhase = ArmPhase.Reaching;
            }

            prevArmLength = currentArmLength;
        }

        // ── Feedback al torso ──────────────────────────────────────────────
        if (torsoController != null)
            torsoController.ReceiveEffort(appliedForce * effortWeight, IsGripped);
    }

    // ──────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Convierte la energía liberada en una fuerza 2D con dos componentes:
    ///   - Tangencial a la superficie (deslizamiento)
    ///   - Normal a la superficie (levantamiento)
    /// La dirección tangencial se infiere de la orientación del brazo en el
    /// momento del empuje: de la mano hacia el hombro proyectada sobre la tangente.
    /// </summary>
    Vector2 ComputeDirectionalForce(float energy)
    {
        Vector2 shoulderPos = shoulder.position;

        // Dirección del empuje: del punto de agarre hacia el hombro
        // (el brazo "empuja" el cuerpo lejos de la mano)
        Vector2 armDir = ((Vector2)shoulder.position - gripPoint).normalized;

        // Tangente de la superficie
        Vector2 tangent = new Vector2(gripNormal.y, -gripNormal.x);

        // Proyecciones
        float tanProj = Vector2.Dot(armDir, tangent);
        float normProj = Vector2.Dot(armDir, gripNormal);

        float tanFrac = 1f - verticalForceFraction;
        Vector2 tanForce = tangent * tanProj * tanFrac;
        Vector2 normForce = gripNormal * normProj * verticalForceFraction * verticalForceBoost;

        // Fuerza total escalada por energía liberada y multiplicador
        Vector2 total = (tanForce + normForce) * energy * forceMultiplier;

        return total;
    }

    // ──────────────────────────────────────────────────────────────────────
    Vector3 GetMouseWorldPos()
    {
        if (mainCam == null) return Vector3.zero;
        Vector3 mp = Input.mousePosition;
        mp.z = Mathf.Abs(mainCam.transform.position.z);
        return mainCam.ScreenToWorldPoint(mp);
    }

    // ── Gizmos ─────────────────────────────────────────────────────────────
    void OnDrawGizmos()
    {
        if (!Application.isPlaying || shoulder == null) return;

        // Color por fase
        switch (CurrentPhase)
        {
            case ArmPhase.Compressing: Gizmos.color = Color.cyan; break;
            case ArmPhase.Extending: Gizmos.color = Color.yellow; break;
            case ArmPhase.Locked: Gizmos.color = Color.red; break;
            case ArmPhase.Reaching: Gizmos.color = Color.green; break;
            default: Gizmos.color = Color.white; break;
        }

        if (ikTarget != null)
            Gizmos.DrawLine(shoulder.position, ikTarget.position);

        if (IsGripped)
        {
            Gizmos.DrawWireSphere(gripPoint, probeRadius + surfacePenetrationOffset);
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(gripPoint, gripNormal * 0.3f);

            // Barra de energía en scene view
            Vector3 barStart = (Vector3)gripPoint + Vector3.up * 0.4f;
            float barLen = StoredEnergy / maxStoredEnergy * 0.8f;
            Gizmos.color = Color.Lerp(Color.green, Color.red, StoredEnergy / maxStoredEnergy);
            Gizmos.DrawLine(barStart, barStart + Vector3.right * barLen);
        }
    }
}