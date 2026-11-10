using System;
using UnityEngine;

namespace Scenario
{
    /// <summary>
    /// A third-person character: walks and runs relative to the camera, turns toward its
    /// direction, jumps, and drives its Animator through the Speed, Grounded and Jump
    /// parameters. Input comes from the keyboard and gamepad, or from a ScenarioAutopilot.
    /// Set up by the Scenario agent's make_playable tool or by hand.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [AddComponentMenu("Scenario/Scenario Player")]
    public class ScenarioPlayer : MonoBehaviour
    {
        public const string SpeedParameter = "Speed";
        public const string GroundedParameter = "Grounded";
        public const string JumpParameter = "Jump";

        [Tooltip("Meters per second when walking.")] public float walkSpeed = 1.6f;
        [Tooltip("Meters per second when running.")] public float runSpeed = 4.5f;
        [Tooltip("Run unless the run key is held (true), or walk unless it is held (false).")] public bool runByDefault;
        [Tooltip("Jump height in meters.")] public float jumpHeight = 1.1f;
        public float gravity = -20f;
        [Tooltip("Degrees per second.")] public float turnSpeed = 600f;
        public Animator animator;
        [Tooltip("Movement is relative to this camera; the main camera when empty.")] public Transform cameraTransform;
        [Tooltip("Bone kept over the character's feet, so clips that travel play in place (path under the Animator).")]
        public string inPlaceBone = "";
        [Tooltip("Below this height the character is put back at its start.")] public float fallLimit = -50f;
        [Tooltip("A drop longer than this, in meters, counts as a fall in playtests.")] public float fallDistance = 4f;
        [Tooltip("Played at each step while moving on the ground: one footstep, or a longer recording sliced into steps.")] public AudioClip footstep;
        [Range(0, 1)] public float footstepVolume = 0.6f;

        /// <summary>Set by an autopilot or a script: when true, the fields below replace the player's input.</summary>
        [NonSerialized] public bool external;
        /// <summary>Movement direction on the ground, in world space, length 0 to 1.</summary>
        [NonSerialized] public Vector3 worldMove;
        [NonSerialized] public bool run;
        [NonSerialized] public bool jump;

        public bool Grounded => _controller != null && _controller.isGrounded;
        public float PlanarSpeed { get; private set; }
        /// <summary>How many times the character dropped further than fallDistance, or fell below fallLimit and was put back.</summary>
        public int Falls { get; private set; }
        public int Jumps { get; private set; }

        static readonly int SpeedId = Animator.StringToHash(SpeedParameter);
        static readonly int GroundedId = Animator.StringToHash(GroundedParameter);
        static readonly int JumpId = Animator.StringToHash(JumpParameter);

        CharacterController _controller;
        Vector3 _start;
        Quaternion _startRotation;
        float _vertical;
        Transform _bone;
        Vector3 _boneRest;
        AudioSource _steps;
        float _stepTimer;
        float _stepStop;
        bool _wasGrounded = true;
        float _airTop;

        void Reset()
        {
            // At high frame rates a step is under the default 1 mm minimum and the controller would not move.
            GetComponent<CharacterController>().minMoveDistance = 0f;
        }

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _start = transform.position;
            _startRotation = transform.rotation;
            if (animator == null) animator = GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.applyRootMotion = false;
                _bone = inPlaceBone.Length > 0 ? animator.transform.Find(inPlaceBone) : null;
                if (_bone != null) _boneRest = animator.transform.InverseTransformPoint(_bone.position);
            }
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (footstep != null)
            {
                _steps = gameObject.AddComponent<AudioSource>();
                _steps.playOnAwake = false;
                _steps.spatialBlend = 0.6f;
            }
        }

        void Update()
        {
            var direction = external ? Vector3.ClampMagnitude(new Vector3(worldMove.x, 0, worldMove.z), 1) : CameraRelative(ScenarioInput.Move());
            var running = external ? run : runByDefault != ScenarioInput.Run();
            var jumping = external ? jump : ScenarioInput.Jump();
            jump = false;

            if (direction.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), turnSpeed * Time.deltaTime);

            if (_controller.isGrounded)
            {
                if (_vertical < 0) _vertical = -2f;
                if (jumping)
                {
                    _vertical = Mathf.Sqrt(-2f * gravity * jumpHeight);
                    Jumps++;
                    if (animator != null) animator.SetTrigger(JumpId);
                }
            }
            _vertical += gravity * Time.deltaTime;

            var speed = (running ? runSpeed : walkSpeed) * direction.magnitude;
            var before = transform.position;
            _controller.Move((direction.normalized * speed + Vector3.up * _vertical) * Time.deltaTime);
            var moved = transform.position - before;
            moved.y = 0;
            PlanarSpeed = Time.deltaTime > 0 ? moved.magnitude / Time.deltaTime : 0;
            CountDrop();

            if (animator != null)
            {
                animator.SetFloat(SpeedId, PlanarSpeed, 0.1f, Time.deltaTime);
                animator.SetBool(GroundedId, _controller.isGrounded);
            }

            if (_steps != null) Footsteps();

            if (transform.position.y < fallLimit)
            {
                Falls++;
                Respawn();
            }
        }

        void CountDrop()
        {
            var grounded = _controller.isGrounded;
            var y = transform.position.y;
            if (!grounded) _airTop = _wasGrounded ? y : Mathf.Max(_airTop, y);
            else if (!_wasGrounded && _airTop - y > fallDistance) Falls++;
            _wasGrounded = grounded;
        }

        void Footsteps()
        {
            if (_stepStop > 0 && Time.time >= _stepStop)
            {
                _steps.Stop();
                _stepStop = 0;
            }
            if (!_controller.isGrounded || PlanarSpeed < 0.3f)
            {
                _stepTimer = 0.12f;
                return;
            }
            _stepTimer -= Time.deltaTime;
            if (_stepTimer > 0) return;
            _stepTimer = Mathf.Lerp(0.55f, 0.32f, Mathf.InverseLerp(walkSpeed, runSpeed, PlanarSpeed));
            _steps.pitch = UnityEngine.Random.Range(0.92f, 1.08f);
            if (footstep.length <= 0.8f)
            {
                _steps.PlayOneShot(footstep, footstepVolume);
                return;
            }
            // A recording of several steps: play a short slice of it.
            _steps.clip = footstep;
            _steps.volume = footstepVolume;
            _steps.time = UnityEngine.Random.Range(0f, footstep.length - 0.3f);
            _steps.Play();
            _stepStop = Time.time + 0.28f;
        }

        void LateUpdate()
        {
            if (_bone == null) return;
            var local = animator.transform.InverseTransformPoint(_bone.position);
            local.x = _boneRest.x;
            local.z = _boneRest.z;
            _bone.position = animator.transform.TransformPoint(local);
        }

        Vector3 CameraRelative(Vector2 input)
        {
            var forward = cameraTransform != null ? Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up) : Vector3.forward;
            if (forward.sqrMagnitude < 0.0001f) forward = transform.forward;
            forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward);
            return Vector3.ClampMagnitude(forward * input.y + right * input.x, 1);
        }

        /// <summary>Back to where the character started.</summary>
        public void Respawn()
        {
            _controller.enabled = false;
            transform.SetPositionAndRotation(_start, _startRotation);
            _controller.enabled = true;
            _vertical = 0;
            _wasGrounded = true;
        }
    }
}
