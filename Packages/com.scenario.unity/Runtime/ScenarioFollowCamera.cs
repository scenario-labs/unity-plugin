using UnityEngine;

namespace Scenario
{
    /// <summary>
    /// A calm third-person camera: stays behind the target at a set distance, orbits with
    /// the mouse (right button) or the right stick, eases back behind the character when
    /// left alone, and moves in front of walls instead of through them.
    /// </summary>
    [AddComponentMenu("Scenario/Scenario Follow Camera")]
    public class ScenarioFollowCamera : MonoBehaviour
    {
        public Transform target;
        public float distance = 4.5f;
        [Tooltip("Height of the point the camera looks at, above the target's feet.")] public float lookHeight = 1.4f;
        [Tooltip("Degrees above the horizon.")] public float pitch = 14f;
        public float minPitch = -10f, maxPitch = 60f;
        [Tooltip("How quickly the camera returns behind the target when not orbited (0 to keep the angle).")] public float recenter = 1.2f;
        [Tooltip("Higher is snappier.")] public float smoothing = 6f;
        [Tooltip("Largest turn per second when following, in degrees. Keeps recordings calm.")] public float maxTurnSpeed = 90f;
        public LayerMask obstacles = ~0;

        float _yaw;
        float _idle;
        Vector3 _velocity;

        void Start()
        {
            if (target != null) _yaw = target.eulerAngles.y;
            Snap();
        }

        /// <summary>Jumps to the resting position behind the target, with no easing.</summary>
        public void Snap()
        {
            if (target == null) return;
            transform.position = Desired(out var look);
            transform.rotation = Quaternion.LookRotation(look - transform.position);
        }

        void LateUpdate()
        {
            if (target == null) return;
            var look = ScenarioInput.Look();
            if (look.sqrMagnitude > 0.0001f)
            {
                _yaw += look.x;
                pitch = Mathf.Clamp(pitch - look.y, minPitch, maxPitch);
                _idle = 0;
            }
            else
            {
                _idle += Time.deltaTime;
                if (recenter > 0 && _idle > 1f)
                {
                    var wanted = Mathf.LerpAngle(_yaw, target.eulerAngles.y, 1f - Mathf.Exp(-recenter * Time.deltaTime));
                    _yaw += Mathf.Clamp(Mathf.DeltaAngle(_yaw, wanted), -maxTurnSpeed * Time.deltaTime, maxTurnSpeed * Time.deltaTime);
                }
            }
            var desired = Desired(out var focus);
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref _velocity, 1f / Mathf.Max(0.1f, smoothing));
            var rotation = Quaternion.LookRotation(focus - transform.position);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, rotation, Mathf.Max(maxTurnSpeed, 30f) * 2f * Time.deltaTime);
        }

        Vector3 Desired(out Vector3 focus)
        {
            focus = target.position + Vector3.up * lookHeight;
            var back = Quaternion.Euler(pitch, _yaw, 0) * Vector3.back;
            var length = distance;
            foreach (var hit in Physics.SphereCastAll(focus, 0.25f, back, distance, obstacles, QueryTriggerInteraction.Ignore))
                if (hit.distance > 0 && !hit.transform.IsChildOf(target))
                    length = Mathf.Min(length, Mathf.Max(0.6f, hit.distance - 0.1f));
            return focus + back * length;
        }
    }
}
