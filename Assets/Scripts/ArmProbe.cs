using UnityEngine;

/// <summary>
/// ArmProbe v9 — impulsos discretos por gesto de windup/release.
///
/// MODELO FUNDAMENTAL:
///   La fuerza ya no es continua. Cada empuje es un evento discreto
///   que requiere completar un ciclo de gesto en dos fases:
///
///   WINDUP:  el mouse se aleja del hombro mientras el brazo está agarrado.
///            Se acumula windupDistance (distancia recorrida en esa dirección).
///
///   RELEASE: el mouse invierte y se acerca al hombro.
///            Cuando el mouse recorre releaseThreshold unidades de vuelta,
///            se dispara UN ÚNICO impulso proporcional al windup acumulado.
///            El brazo entra en Cooldown y no puede generar fuerza hasta
///            que expire cooldownTime, independientemente del movimiento del mouse.
///
/// POR QUÉ ESTO CIERRA EL EXPLOIT:
///   - "Seguir al personaje con el mouse" no aleja el mouse del hombro
///     en términos absolutos de mundo; si el personaje se mueve y el mouse
///     lo sigue, la distancia mouse-hombro cambia poco → no hay windup real.
///   - Incluso si se logra hacer windup, el cooldown bloquea cualquier fuerza
///     siguiente hasta que el timer expire, eliminando la fuerza continua.
///   - El impulso es proporcional al windup: gestos pequeños = impulsos pequeños.
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
    [Range(0f, 0.5f)]
    public float surfacePenetrationOffset = 0f;

    [Header("Suavizado IK target")]
    [Range(4f, 30f)]
    public float ikFollowSpeed = 14f;

    // ── Gesto de windup / release ──────────────────────────────────────────
    [Header("Gesto windup / release")]

    [Tooltip(
        "Distancia mínima (unidades mundo) que el mouse debe alejarse del hombro " +
        "para iniciar la fase de windup. Evita activaciones accidentales.\n" +
        "Recomendado: 0.10 – 0.20.")]
    [Range(0.05f, 0.5f)]
    public float windupStartThreshold = 0.12f;

    [Tooltip(
        "Distancia mínima de windup acumulada (unidades mundo) para que el " +
        "release genere impulso. Debajo de este valor → gesto cancelado sin fuerza.\n" +
        "Recomendado: 0.15 – 0.40.")]
    [Range(0.05f, 1.0f)]
    public float minWindupDistance = 0.20f;

    [Tooltip(
        "Distancia máxima de windup. El exceso se descarta (cap de fuerza).\n" +
        "Recomendado: 0.5 – 1.2.")]
    [Range(0.2f, 2.0f)]
    public float maxWindupDistance = 0.80f;

    [Tooltip(
        "Distancia que el mouse debe recorrer de vuelta hacia el hombro para " +
        "disparar el impulso. Umbral de release.\n" +
        "Recomendado: 0.08 – 0.20.")]
    [Range(0.02f, 0.5f)]
    public float releaseThreshold = 0.12f;

    [Tooltip(
        "Tiempo en segundos durante el cual el brazo no puede iniciar un nuevo " +
        "windup después de haber disparado un impulso. Elimina la fuerza continua.\n" +
        "Recomendado: 0.15 – 0.35.")]
    [Range(0.05f, 1.0f)]
    public float cooldownTime = 0.22f;

    [Tooltip(
        "Si el mouse se detiene durante el windup (velocidad menor a este valor), " +
        "el windup se cancela y el brazo vuelve a Idle sin impulso.\n" +
        "Recomendado: 0.04 – 0.10.")]
    [Range(0.01f, 0.3f)]
    public float windupCancelSpeed = 0.06f;

    // ── Magnitud del impulso ───────────────────────────────────────────────
    [Header("Magnitud del impulso")]

    [Tooltip("Fuerza por unidad de windupDistance acumulada.")]
    public float impulseGainPerUnit = 20f;

    [Tooltip("Impulso máximo (clampeado antes de aplicar).")]
    public float maxImpulse = 30f;

    [Tooltip(
        "Boost sobre la componente Y del impulso. " +
        "Compensa la gravedad para facilitar el salto. Recomendado: 1.3 – 2.0.")]
    [Range(0.5f, 3f)]
    public float verticalBoost = 1.5f;

    // ── Dirección del impulso ──────────────────────────────────────────────
    [Header("Dirección del impulso")]

    [Tooltip(
        "Peso de la dirección del brazo (gripPoint→shoulder) en la mezcla con " +
        "la dirección del gesto del mouse. 1 = solo brazo. 0 = solo mouse.\n" +
        "Recomendado: 0.5 – 0.7.")]
    [Range(0f, 1f)]
    public float armDirectionWeight = 0.55f;

    // ── Requisito de compresión ────────────────────────────────────────────
    [Header("Requisito de compresión")]

    [Tooltip("Ratio de extensión a partir del cual el brazo está demasiado " +
             "extendido para hacer windup. El gesto se ignora.")]
    [Range(0.6f, 1.0f)]
    public float extensionLockRatio = 0.88f;

    [Tooltip("Longitud de reposo del brazo (fracción de maxReach). " +
             "La eficiencia del impulso se escala por cuánto comprimido está el brazo.")]
    [Range(0.3f, 0.9f)]
    public float restLengthRatio = 0.60f;

    // ── Feedback al torso ──────────────────────────────────────────────────
    [Header("Feedback al torso")]
    [Range(0f, 2f)]
    public float effortWeight = 1f;

    // ── Estado público ─────────────────────────────────────────────────────
    public bool IsGripped { get; private set; }
    public float CurrentArmRatio { get; private set; }
    public PushPhase CurrentPhase { get; private set; }
    public float WindupProgress { get; private set; }   // 0–1, para HUD
    public float LastImpulse { get; private set; }

    public enum PushPhase { Free, Reaching, Idle, Windup, Release, Cooldown, Locked }

    // ── Privadas ───────────────────────────────────────────────────────────
    private Vector2 gripPoint;
    private Vector2 gripNormal;
    private Vector3 smoothedTargetPos;
    private Camera mainCam;
    private bool buttonHeld;

    // Posición del mouse en mundo, frame anterior (FixedUpdate)
    private Vector2 prevMouseWorld;

    // Distancia mouse–hombro en el frame anterior (para detectar alejamiento/acercamiento)
    private float prevMouseShoulderDist;

    // Acumulador de windup
    private float windupDistance;

    // Distancia devuelta desde el pico del windup (para detectar release)
    private float releaseProgress;

    // Distancia máxima del mouse al hombro alcanzada en este windup
    private float windupPeakDist;

    // Timer de cooldown
    private float cooldownTimer;

    // Velocidad del mouse (suavizada, para detección de pausa)
    private Vector2 smoothedMouseVel;

    // ──────────────────────────────────────────────────────────────────────
    void Start()
    {
        mainCam = Camera.main;
        if (ikTarget != null) smoothedTargetPos = ikTarget.position;
        prevMouseWorld = GetMouseWorldPos2D();
    }

    void Update()
    {
        buttonHeld = Input.GetMouseButton(mouseButton);
    }

    void FixedUpdate()
    {
        if (shoulder == null || ikTarget == null || physicsBody == null) return;

        Vector2 shoulderPos = shoulder.position;
        Vector2 mouseWorldNow = GetMouseWorldPos2D();

        // ── Velocidad del mouse (suavizada) ───────────────────────────────
        Vector2 rawVel = (mouseWorldNow - prevMouseWorld) / Time.fixedDeltaTime;
        smoothedMouseVel = Vector2.Lerp(smoothedMouseVel, rawVel, 0.35f);
        prevMouseWorld = mouseWorldNow;

        // ── Distancia mouse–hombro actual ─────────────────────────────────
        float mouseShoulderDist = Vector2.Distance(mouseWorldNow, shoulderPos);

        // ── Probe ──────────────────────────────────────────────────────────
        Vector2 toMouse = mouseWorldNow - shoulderPos;
        float dist = Mathf.Clamp(toMouse.magnitude, minReach, maxReach);
        Vector2 probeDir = toMouse.sqrMagnitude > 0.0001f
                                    ? toMouse.normalized : Vector2.right;

        RaycastHit2D hit = Physics2D.CircleCast(
            shoulderPos, probeRadius, probeDir, dist, surfaceLayer);
        bool surfaceHit = hit.collider != null;

        // ── Ratio de extensión del brazo ──────────────────────────────────
        float armLen = Vector2.Distance(shoulderPos, (Vector2)ikTarget.position);
        CurrentArmRatio = armLen / maxReach;

        // Factor de compresión: 1 en reposo o más corto, 0 en extensionLockRatio
        float compFactor = 1f - Mathf.Clamp01(
            Mathf.InverseLerp(restLengthRatio, extensionLockRatio, CurrentArmRatio));

        // ── Lógica de agarre ───────────────────────────────────────────────
        if (!IsGripped)
        {
            if (buttonHeld && surfaceHit)
            {
                IsGripped = true;
                gripPoint = hit.point;
                gripNormal = hit.normal;
                windupDistance = 0f;
                releaseProgress = 0f;
                cooldownTimer = 0f;
                CurrentPhase = PushPhase.Idle;

                Vector2 pen = hit.point - hit.normal * surfacePenetrationOffset;
                smoothedTargetPos = new Vector3(pen.x, pen.y, ikTarget.position.z);
                prevMouseShoulderDist = mouseShoulderDist;
                prevMouseWorld = mouseWorldNow;
                smoothedMouseVel = Vector2.zero;
            }
        }
        else
        {
            float distToGrip = Vector2.Distance(shoulderPos, gripPoint);
            if (!buttonHeld || distToGrip > maxReach * 1.15f)
            {
                IsGripped = false;
                windupDistance = 0f;
                releaseProgress = 0f;
                cooldownTimer = 0f;
                CurrentPhase = PushPhase.Free;
            }
        }

        // ── IK target ─────────────────────────────────────────────────────
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
            CurrentPhase = surfaceHit ? PushPhase.Reaching : PushPhase.Free;
        }

        ikTarget.position = smoothedTargetPos;

        // ── Máquina de estados de empuje ───────────────────────────────────
        Vector2 appliedImpulse = Vector2.zero;
        LastImpulse = 0f;

        if (IsGripped)
        {
            // Cambio de distancia mouse–hombro en este frame
            float distDelta = mouseShoulderDist - prevMouseShoulderDist;

            // ── Cooldown ───────────────────────────────────────────────────
            if (CurrentPhase == PushPhase.Cooldown)
            {
                cooldownTimer -= Time.fixedDeltaTime;
                if (cooldownTimer <= 0f)
                {
                    CurrentPhase = PushPhase.Idle;
                    windupDistance = 0f;
                    releaseProgress = 0f;
                }
            }
            // ── Locked (brazo demasiado extendido) ─────────────────────────
            else if (compFactor <= 0f)
            {
                CurrentPhase = PushPhase.Locked;
                windupDistance = 0f;
            }
            // ── Idle → espera inicio de windup ─────────────────────────────
            else if (CurrentPhase == PushPhase.Idle)
            {
                // El mouse debe alejarse del hombro más de windupStartThreshold
                if (distDelta > 0f && mouseShoulderDist > windupStartThreshold)
                {
                    CurrentPhase = PushPhase.Windup;
                    windupDistance = 0f;
                    windupPeakDist = mouseShoulderDist;
                    releaseProgress = 0f;
                }
            }
            // ── Windup ─────────────────────────────────────────────────────
            else if (CurrentPhase == PushPhase.Windup)
            {
                float mouseSpeed = smoothedMouseVel.magnitude;

                // Cancelar si el mouse se detiene
                if (mouseSpeed < windupCancelSpeed)
                {
                    CurrentPhase = PushPhase.Idle;
                    windupDistance = 0f;
                    releaseProgress = 0f;
                }
                else if (distDelta > 0f)
                {
                    // Mouse sigue alejándose: acumular windup
                    windupDistance = Mathf.Min(
                        windupDistance + distDelta, maxWindupDistance);
                    windupPeakDist = mouseShoulderDist;
                }
                else if (distDelta < 0f)
                {
                    // Mouse empieza a acercarse: transición a Release
                    if (windupDistance >= minWindupDistance)
                    {
                        CurrentPhase = PushPhase.Release;
                        releaseProgress = 0f;
                    }
                    else
                    {
                        // Windup insuficiente → cancelar
                        CurrentPhase = PushPhase.Idle;
                        windupDistance = 0f;
                    }
                }

                WindupProgress = Mathf.Clamp01(windupDistance / maxWindupDistance);
            }
            // ── Release ───────────────────────────────────────────────────
            else if (CurrentPhase == PushPhase.Release)
            {
                if (distDelta < 0f)
                {
                    // Mouse avanzando de vuelta hacia el hombro
                    releaseProgress += -distDelta;

                    if (releaseProgress >= releaseThreshold)
                    {
                        // ¡Impulso! — único evento de fuerza por gesto
                        appliedImpulse = ComputeImpulse(
                            shoulderPos, windupDistance, compFactor);

                        physicsBody.ApplyGripForce(appliedImpulse, gripPoint);
                        LastImpulse = appliedImpulse.magnitude;

                        // Reset y Cooldown
                        cooldownTimer = cooldownTime;
                        windupDistance = 0f;
                        releaseProgress = 0f;
                        WindupProgress = 0f;
                        CurrentPhase = PushPhase.Cooldown;
                    }
                }
                else if (distDelta > 0f)
                {
                    // Mouse volvió a alejarse antes de completar release → re-windup
                    CurrentPhase = PushPhase.Windup;
                    releaseProgress = 0f;
                    // Conservar windupDistance acumulado
                }
            }
        }

        // ── Feedback al torso ──────────────────────────────────────────────
        if (torsoController != null)
        {
            Vector2 gripOffset = gripPoint - (Vector2)shoulder.position;
            torsoController.ReceiveEffort(appliedImpulse * effortWeight, IsGripped, gripOffset);
        }

        prevMouseShoulderDist = mouseShoulderDist;
    }

    // ──────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Calcula el vector de impulso único a partir del windup acumulado.
    /// Dirección: mezcla brazo + dirección del gesto (desde el pico del windup).
    /// Magnitud: proporcional al windupDistance × compressionFactor.
    /// </summary>
    Vector2 ComputeImpulse(Vector2 shoulderPos, float windup, float compFactor)
    {
        // Dirección del brazo: del grip hacia el hombro
        Vector2 armDir = (shoulderPos - gripPoint).normalized;

        // Dirección del gesto: usamos la velocidad suavizada del mouse
        // en el momento del release (apunta hacia el hombro si el gesto es correcto)
        Vector2 mouseDir = smoothedMouseVel.sqrMagnitude > 0.0001f
                            ? smoothedMouseVel.normalized
                            : armDir;

        // Mezcla ponderada y renormalización
        Vector2 blendDir = (armDir * armDirectionWeight +
                            mouseDir * (1f - armDirectionWeight));
        if (blendDir.sqrMagnitude < 0.0001f) blendDir = armDir;
        blendDir = blendDir.normalized;

        // Magnitud: windup × ganancia × compresión
        float magnitude = Mathf.Min(
            windup * impulseGainPerUnit * compFactor,
            maxImpulse);

        Vector2 impulse = blendDir * magnitude;

        // Boost vertical sobre componente Y
        impulse.y *= verticalBoost;

        return impulse;
    }

    // ──────────────────────────────────────────────────────────────────────
    Vector2 GetMouseWorldPos2D()
    {
        if (mainCam == null) return Vector2.zero;
        Vector3 mp = Input.mousePosition;
        mp.z = Mathf.Abs(mainCam.transform.position.z);
        return mainCam.ScreenToWorldPoint(mp);
    }

    // ── Gizmos ─────────────────────────────────────────────────────────────
    void OnDrawGizmos()
    {
        if (!Application.isPlaying || shoulder == null) return;

        switch (CurrentPhase)
        {
            case PushPhase.Windup: Gizmos.color = Color.cyan; break;
            case PushPhase.Release: Gizmos.color = Color.yellow; break;
            case PushPhase.Cooldown: Gizmos.color = Color.grey; break;
            case PushPhase.Locked: Gizmos.color = Color.red; break;
            case PushPhase.Reaching: Gizmos.color = Color.green; break;
            default: Gizmos.color = Color.white; break;
        }

        if (ikTarget != null)
            Gizmos.DrawLine(shoulder.position, ikTarget.position);

        if (IsGripped)
        {
            Vector2 shoulderPos = shoulder.position;

            // Punto de agarre y normal
            Gizmos.DrawWireSphere(gripPoint, probeRadius + surfacePenetrationOffset);
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(gripPoint, gripNormal * 0.3f);

            // Barra de windup progress (crece horizontalmente desde el grip)
            if (CurrentPhase == PushPhase.Windup || CurrentPhase == PushPhase.Release)
            {
                Vector3 barStart = (Vector3)gripPoint + Vector3.up * 0.5f;
                float barLen = WindupProgress * 0.8f;
                Gizmos.color = Color.Lerp(Color.green, Color.magenta, WindupProgress);
                Gizmos.DrawLine(barStart, barStart + Vector3.right * barLen);
            }

            // Disco de cooldown (radio decrece con el timer)
            if (CurrentPhase == PushPhase.Cooldown)
            {
                float r = (cooldownTimer / cooldownTime) * 0.2f;
                Gizmos.color = Color.grey;
                Gizmos.DrawWireSphere(gripPoint, r);
            }

            // Vector velocidad del mouse
            Gizmos.color = Color.magenta;
            Gizmos.DrawRay(
                (Vector3)gripPoint + Vector3.up * 0.15f,
                (Vector3)smoothedMouseVel * 0.12f);
        }
    }
}