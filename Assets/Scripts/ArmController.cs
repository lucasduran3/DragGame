// ArmController.cs — full file replacement

using UnityEngine;

public class ArmController : MonoBehaviour
{
    public enum ArmSide { Front, Back }

    [Header("Identity")]
    public ArmSide side;

    [Header("IK")]
    public Transform ikTarget;

    [Header("Reach")]
    [SerializeField] private float maxReach = 2.5f;
    [SerializeField] private float minReach = 0.3f;
    [SerializeField] private float ikFollowSpeed = 20f;

    [Header("Grab")]
    [SerializeField] private float grabRadius = 0.3f;
    [SerializeField] private LayerMask grabbableLayers;

    [Header("Hand Collider")]
    [SerializeField] private float handColliderRadius = 0.15f;
    [SerializeField] private float maxPenetrationDepth = 0.04f;

    [Header("Force")]
    [SerializeField] private float pullForce = 22f;
    [SerializeField] private float maxForcePerFrame = 24f;
    [SerializeField] private float extensionForceScale = 1.3f;
    [SerializeField] private float swingForceMultiplier = 1.2f;

    [Header("Arm Extension")]
    [SerializeField] private float extensionSpeed = 8f;

    [Header("Circular Motion")]
    [SerializeField] private float mouseDeltaThreshold = 0.01f;
    [SerializeField] private float circularityThreshold = 0.3f;
    [SerializeField] private int motionSampleCount = 12;
    [SerializeField] private float minForceFromCircularity = 0.15f;

    private Rigidbody2D _bodyRb;
    private Transform _bodyTransform;

    private Vector2 _targetWorldPos;
    private Vector2 _ikTargetPos;

    private bool _isGrabbing;
    private Vector2 _grabPoint;

    private DistanceJoint2D _swingJoint;
    private bool _isSwinging;

    private int _mouseButton;

    private Vector2 _surfaceNormal;
    private bool _handOnSurface;
    private Vector2 _resolvedHandPos;

    private float _currentExtension;
    private Vector2 _currentArmDirection;

    private Vector2[] _mouseSamples;
    private int _sampleIndex;
    private Vector2 _lastMouseWorld;
    private float _circularityScore;

    // Cached per-frame mouse delta for use in FixedUpdate
    private Vector2 _mouseDeltaThisFrame;

    private void Awake()
    {
        var controller = GetComponentInParent<ArmLocomotionController>();
        _bodyRb = controller.rb;
        _bodyTransform = controller.transform;
        _mouseButton = side == ArmSide.Front ? 0 : 1;
        _ikTargetPos = ikTarget != null
            ? (Vector2)ikTarget.position
            : (Vector2)_bodyTransform.position;

        _mouseSamples = new Vector2[motionSampleCount];
        _lastMouseWorld = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        _currentExtension = minReach;
        _currentArmDirection = side == ArmSide.Front ? Vector2.right : Vector2.left;
    }

    private void Update()
    {
        Vector2 mouseWorld = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        _mouseDeltaThisFrame = mouseWorld - _lastMouseWorld;

        TrackMouseMotion(mouseWorld);

        if (Input.GetMouseButtonDown(_mouseButton))
            TryGrab(mouseWorld);

        if (Input.GetMouseButtonUp(_mouseButton))
            Release();

        UpdateArmExtension(mouseWorld);
        UpdateTargetPosition(mouseWorld);

        _lastMouseWorld = mouseWorld;
    }

    private void FixedUpdate()
    {
        if (_isGrabbing && !_isSwinging)
            ApplyDirectionalForce();

        if (_isSwinging)
            UpdateSwing();

        ResolveHandPenetration();
        SmoothMoveIKTarget();
    }

    private void UpdateArmExtension(Vector2 mouseWorld)
    {
        Vector2 shoulderPos = _bodyTransform.position;
        Vector2 toMouse = mouseWorld - shoulderPos;
        float mouseDist = toMouse.magnitude;

        if (!_isGrabbing)
        {
            _currentExtension = Mathf.Clamp(mouseDist, minReach, maxReach);
            if (mouseDist > 0.001f)
                _currentArmDirection = toMouse.normalized;
            return;
        }

        if (_mouseDeltaThisFrame.magnitude < mouseDeltaThreshold) return;

        Vector2 inputDir = _mouseDeltaThisFrame.normalized;

        // Arm axis: shoulder → grab point
        Vector2 shoulderToGrab = _grabPoint - (Vector2)_bodyTransform.position;
        float grabDist = shoulderToGrab.magnitude;
        if (grabDist < 0.001f) return;

        Vector2 armAxis = shoulderToGrab.normalized;
        _currentArmDirection = armAxis;

        // Project input onto arm axis:
        // +1 = input toward grab (pull/flex)
        // -1 = input away from grab (push/extend backward)
        float projection = Vector2.Dot(inputDir, armAxis);

        // Negate: forward mouse (away from grab) should INCREASE extension
        // so arm stretches backward and body is pushed forward
        float extensionDelta = -projection * extensionSpeed * Time.deltaTime;
        _currentExtension = Mathf.Clamp(
            _currentExtension + extensionDelta,
            minReach,
            maxReach);
    }

    private void ApplyDirectionalForce()
    {
        if (!_isGrabbing) return;

        Vector2 bodyPos = _bodyTransform.position;
        Vector2 toGrab = _grabPoint - bodyPos;
        float dist = toGrab.magnitude;
        if (dist < minReach) return;

        Vector2 armAxis = toGrab.normalized;

        // Determine input intent from cached mouse delta
        float inputProjection = 0f;
        if (_mouseDeltaThisFrame.magnitude > mouseDeltaThreshold)
            inputProjection = Vector2.Dot(_mouseDeltaThisFrame.normalized, armAxis);

        // inputProjection > 0: pulling toward grab
        // inputProjection < 0: pushing away from grab (forward movement)
        // Force direction is always driven by input, not by arm state
        Vector2 forceDir = inputProjection >= 0f ? armAxis : -armAxis;

        float extensionRatio = Mathf.InverseLerp(minReach, maxReach, _currentExtension);
        float forceMultiplier = GetForceMultiplier();

        float forceMag = pullForce
            * Mathf.Clamp01(dist / maxReach)
            * forceMultiplier
            * extensionForceScale;

        // Push force scales with extension ratio so fully flexed arms can push hard
        if (inputProjection < 0f)
            forceMag *= Mathf.Lerp(1f, 1.5f, 1f - extensionRatio);

        Vector2 force = ClampForce(forceDir * forceMag);
        _bodyRb.AddForce(force, ForceMode2D.Force);
    }

    private void UpdateTargetPosition(Vector2 mouseWorld)
    {
        Vector2 shoulderPos = _bodyTransform.position;

        if (_isGrabbing)
        {
            // Hand is pinned at resolved grab point; body moves, not the hand
            _targetWorldPos = _resolvedHandPos;
        }
        else
        {
            Vector2 dir = mouseWorld - shoulderPos;
            float dist = Mathf.Clamp(dir.magnitude, minReach, maxReach);
            _targetWorldPos = shoulderPos + dir.normalized * dist;
            _resolvedHandPos = _targetWorldPos;
            if (dir.magnitude > 0.001f)
                _currentArmDirection = dir.normalized;
        }
    }

    private void TrackMouseMotion(Vector2 mouseWorld)
    {
        Vector2 delta = mouseWorld - _lastMouseWorld;
        if (delta.magnitude < mouseDeltaThreshold) return;

        _mouseSamples[_sampleIndex % motionSampleCount] = delta.normalized;
        _sampleIndex++;

        if (_sampleIndex >= motionSampleCount)
            _circularityScore = ComputeCircularity();
    }

    private float ComputeCircularity()
    {
        float signedArea = 0f;
        float totalMag = 0f;

        for (int i = 0; i < motionSampleCount; i++)
        {
            Vector2 a = _mouseSamples[i];
            Vector2 b = _mouseSamples[(i + 1) % motionSampleCount];
            signedArea += a.x * b.y - a.y * b.x;
            totalMag += a.magnitude + b.magnitude;
        }

        if (totalMag < 0.0001f) return 0f;
        return Mathf.Clamp01(Mathf.Abs(signedArea) / (totalMag * 0.5f));
    }

    private float GetForceMultiplier()
    {
        if (_circularityScore < circularityThreshold) return minForceFromCircularity;
        float t = Mathf.InverseLerp(circularityThreshold, 1f, _circularityScore);
        return Mathf.Lerp(minForceFromCircularity, 1f, t);
    }

    private void TryGrab(Vector2 mouseWorld)
    {
        Vector2 shoulderPos = _bodyTransform.position;
        Vector2 dir = mouseWorld - shoulderPos;
        float dist = Mathf.Clamp(dir.magnitude, minReach, maxReach);
        Vector2 clampedTarget = shoulderPos + dir.normalized * dist;

        Collider2D hit = Physics2D.OverlapCircle(clampedTarget, grabRadius, grabbableLayers);

        if (hit != null)
        {
            _isGrabbing = true;
            _grabPoint = clampedTarget;
            _targetWorldPos = _grabPoint;
            _resolvedHandPos = _grabPoint;
            _currentExtension = dist;
            _currentArmDirection = dir.normalized;
            _circularityScore = 0f;
            _sampleIndex = 0;

            if (dist > maxReach * 0.6f)
                StartSwing();
        }
        else
        {
            _isGrabbing = false;
        }
    }

    private void Release()
    {
        _isGrabbing = false;
        _isSwinging = false;
        _handOnSurface = false;
        _circularityScore = 0f;
        _sampleIndex = 0;

        if (_swingJoint != null)
        {
            Destroy(_swingJoint);
            _swingJoint = null;
        }
    }

    private void StartSwing()
    {
        _isSwinging = true;
        _swingJoint = _bodyRb.gameObject.AddComponent<DistanceJoint2D>();
        _swingJoint.autoConfigureDistance = false;
        _swingJoint.connectedAnchor = _grabPoint;
        _swingJoint.distance = Mathf.Max(
            Vector2.Distance(_bodyTransform.position, _grabPoint), 0.1f);
        _swingJoint.enableCollision = true;
        _swingJoint.maxDistanceOnly = true;
    }

    private void UpdateSwing()
    {
        if (_swingJoint == null) return;

        Vector2 mouseWorld = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Vector2 toMouse = mouseWorld - (Vector2)_bodyTransform.position;
        float multiplier = GetForceMultiplier() * swingForceMultiplier;
        Vector2 force = ClampForce(toMouse.normalized * (pullForce * multiplier));
        _bodyRb.AddForce(force, ForceMode2D.Force);
    }

    private void ResolveHandPenetration()
    {
        if (!_isGrabbing)
        {
            _handOnSurface = false;
            return;
        }

        Vector2 handPos = _resolvedHandPos;
        Collider2D[] results = new Collider2D[4];

        // 2. Configuramos el filtro (esto reemplaza el simple int del LayerMask)
        ContactFilter2D filter = new ContactFilter2D();
        filter.SetLayerMask(grabbableLayers);
        filter.useLayerMask = true;

        // 3. Usamos el nuevo método OverlapCircle (que ahora incluye el comportamiento NonAlloc)
        int count = Physics2D.OverlapCircle(handPos, handColliderRadius, filter, results);

        _handOnSurface = count > 0;
        if (!_handOnSurface) return;

        float closestDist = float.MaxValue;
        Vector2 bestNormal = Vector2.up;

        for (int i = 0; i < count; i++)
        {
            if (results[i] == null) continue;
            Vector2 closest = results[i].ClosestPoint(handPos);
            float d = Vector2.Distance(handPos, closest);
            if (d < closestDist)
            {
                closestDist = d;
                Vector2 raw = handPos - closest;
                bestNormal = raw.sqrMagnitude > 0.0001f ? raw.normalized : Vector2.up;
            }
        }

        _surfaceNormal = bestNormal;
        float penetration = handColliderRadius - closestDist;
        if (penetration > maxPenetrationDepth)
        {
            float excess = penetration - maxPenetrationDepth;
            _resolvedHandPos = handPos + bestNormal * excess;
            _grabPoint = _resolvedHandPos;
        }
    }

    private void SmoothMoveIKTarget()
    {
        _ikTargetPos = Vector2.MoveTowards(
            _ikTargetPos,
            _targetWorldPos,
            ikFollowSpeed * Time.fixedDeltaTime);
        ikTarget.position = _ikTargetPos;
    }

    private Vector2 ClampForce(Vector2 force)
    {
        return force.magnitude > maxForcePerFrame
            ? force.normalized * maxForcePerFrame
            : force;
    }

    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = _isGrabbing ? Color.green : Color.red;
        Gizmos.DrawWireSphere(_grabPoint, grabRadius);
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(_resolvedHandPos, handColliderRadius);
        if (_handOnSurface)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(_resolvedHandPos, _surfaceNormal * 0.3f);
        }
        Gizmos.color = Color.Lerp(Color.red, Color.green, _circularityScore);
        Gizmos.DrawWireSphere(_bodyTransform.position, 0.2f);
        Gizmos.color = Color.magenta;
        Gizmos.DrawLine(
            _bodyTransform.position,
            (Vector2)_bodyTransform.position + _currentArmDirection * _currentExtension);
    }
}