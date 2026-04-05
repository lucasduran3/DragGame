using UnityEngine;

public class AlienLocomotion : MonoBehaviour
{
    [Header("Referencias Físicas")]
    [Tooltip("El Rigidbody del Torso. Aplicar la fuerza aquí genera una inclinación natural.")]
    public Rigidbody2D torsoRb;

    [Header("Configuración de Fuerza")]
    public float armLength = 3f;
    public float armStrength = 150f;
    public float handRadius = 0.2f;

    [Header("Referencias IK")]
    public Transform frontArmIKTarget;
    public Transform backArmIKTarget;
    public Vector2 backArmOffset = new Vector2(0.5f, -0.3f);

    [Header("Entorno")]
    public LayerMask groundLayer;

    private Camera mainCamera;

    void Start()
    {
        mainCamera = Camera.main;
    }

    void FixedUpdate()
    {
        // 1. Obtener la posición deseada por el jugador (Mouse)
        Vector2 mouseWorldPos = mainCamera.ScreenToWorldPoint(Input.mousePosition);
        Vector2 bodyPos = torsoRb.position;

        // 2. Limitar el alcance del brazo (el mouse no puede estirar el brazo infinitamente)
        Vector2 directionToMouse = mouseWorldPos - bodyPos;
        Vector2 desiredPos = bodyPos + Vector2.ClampMagnitude(directionToMouse, armLength);

        // 3. Comprobar si hay suelo en la trayectoria del brazo (Simulación de colisión de la mano)
        Vector2 directionToDesired = desiredPos - bodyPos;
        float distance = directionToDesired.magnitude;

        // Usamos CircleCast para darle volumen a la mano y que no atraviese esquinas afiladas
        RaycastHit2D hit = Physics2D.CircleCast(bodyPos, handRadius, directionToDesired.normalized, distance, groundLayer);

        Vector2 frontHandPos;

        if (hit.collider != null)
        {
            // ESTADO: APOYADO/ARRASTRANDO
            // La mano choca contra el suelo y se queda anclada en el punto de contacto.
            frontHandPos = hit.point;

            // LA MAGIA DE LA FÍSICA:
            // Calculamos la tensión. Es la diferencia entre donde está apoyada la mano y donde el jugador QUERÍA que estuviera.
            Vector2 tension = hit.point - desiredPos;

            // Convertimos esa tensión en fuerza física y la aplicamos al torso.
            Vector2 force = tension * armStrength;
            torsoRb.AddForce(force);
        }
        else
        {
            // ESTADO: EN EL AIRE
            // La mano no toca nada, se mueve libremente hacia donde apunta el mouse.
            frontHandPos = desiredPos;
        }

        // 4. Actualizar la posición de los IK Targets para que los huesos visuales sigan a la lógica
        frontArmIKTarget.position = frontHandPos;

        // Para el brazo trasero, aplicamos un comportamiento de "apoyo" o seguidor con un pequeño offset
        // Utilizamos Lerp para que el brazo secundario se mueva con cierta suavidad/retraso orgánico
        Vector2 backHandDesiredPos = frontHandPos - backArmOffset;
        backArmIKTarget.position = Vector2.Lerp(backArmIKTarget.position, backHandDesiredPos, Time.fixedDeltaTime * 15f);
    }
}