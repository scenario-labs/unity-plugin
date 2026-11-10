using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scenario
{
    /// <summary>
    /// Walks a ScenarioPlayer through waypoints: for playtests (does the character reach
    /// each point, does it fall, does it get stuck) and for recording gameplay.
    /// </summary>
    [RequireComponent(typeof(ScenarioPlayer))]
    [AddComponentMenu("Scenario/Scenario Autopilot")]
    public class ScenarioAutopilot : MonoBehaviour
    {
        [Serializable]
        public class Waypoint
        {
            public Vector3 position;
            [Tooltip("Run to this point.")] public bool run;
            [Tooltip("Jump when this point is reached.")] public bool jump;
            [Tooltip("Seconds to wait at this point.")] public float wait;
        }

        public List<Waypoint> waypoints = new List<Waypoint>();
        [Tooltip("A point counts as reached within this distance on the ground, in meters.")] public float arriveRadius = 0.6f;
        [Tooltip("Seconds without progress before a point is given up.")] public float stuckLimit = 3f;

        public int Current { get; private set; }
        public bool Done { get; private set; }
        public float Elapsed { get; private set; }
        /// <summary>Seconds from the start when each waypoint was reached; negative when given up.</summary>
        public List<float> ReachedAt { get; } = new List<float>();
        public float LongestStuck { get; private set; }

        ScenarioPlayer _player;
        float _stuck;
        float _waited;
        float _bestDistance = float.MaxValue;

        void OnEnable()
        {
            _player = GetComponent<ScenarioPlayer>();
            _player.external = true;
        }

        void OnDisable()
        {
            if (_player == null) return;
            _player.worldMove = Vector3.zero;
            _player.external = false;
        }

        void Update()
        {
            if (Done) return;
            Elapsed += Time.deltaTime;
            if (Current >= waypoints.Count)
            {
                Finish();
                return;
            }
            var point = waypoints[Current];
            var offset = point.position - transform.position;
            offset.y = 0;
            var distance = offset.magnitude;
            if (distance <= arriveRadius)
            {
                // A jump point is taken in stride; a waiting point stops the character.
                if (point.jump && _waited == 0) _player.jump = true;
                if (point.wait > 0)
                {
                    _player.worldMove = Vector3.zero;
                    _waited += Time.deltaTime;
                    if (_waited < point.wait) return;
                }
                Next(Elapsed);
                return;
            }
            // Ease in to the last point and to waiting points so they are not overshot.
            var stops = point.wait > 0 || Current == waypoints.Count - 1;
            var pace = stops ? Mathf.Clamp01(distance / Mathf.Max(arriveRadius * 2f, 1f) + 0.35f) : 1f;
            _player.worldMove = offset.normalized * pace;
            _player.run = point.run;

            if (distance < _bestDistance - 0.05f)
            {
                _bestDistance = distance;
                _stuck = 0;
            }
            else
            {
                _stuck += Time.deltaTime;
                LongestStuck = Mathf.Max(LongestStuck, _stuck);
                if (_stuck > stuckLimit) Next(-Elapsed);
            }
        }

        void Next(float reachedAt)
        {
            ReachedAt.Add(reachedAt);
            Current++;
            _stuck = 0;
            _waited = 0;
            _bestDistance = float.MaxValue;
        }

        void Finish()
        {
            Done = true;
            _player.worldMove = Vector3.zero;
        }
    }
}
