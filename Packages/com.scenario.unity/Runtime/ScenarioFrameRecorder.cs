using System.IO;
using UnityEngine;

namespace Scenario
{
    /// <summary>
    /// Records a camera to numbered PNG frames in Play mode. Game time advances exactly one
    /// frame per image (Time.captureFramerate), so the recording is smooth whatever the
    /// machine's speed. Encode the folder with ffmpeg: -framerate 30 -i %05d.png.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [AddComponentMenu("Scenario/Scenario Frame Recorder")]
    public class ScenarioFrameRecorder : MonoBehaviour
    {
        public Camera source;
        public string folder = "Recordings";
        public int width = 1920;
        public int height = 1080;
        public int frameRate = 30;
        [Tooltip("Stops after this many seconds of game time (0: until disabled).")] public float seconds;

        public int Frames { get; private set; }
        public bool Done { get; private set; }

        RenderTexture _target;
        Texture2D _image;
        int _previousRate;

        // Set up on the first frame, so a script that adds the component can set its fields first.
        void Begin()
        {
            if (source == null) source = Camera.main;
            Directory.CreateDirectory(folder);
            _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            _image = new Texture2D(width, height, TextureFormat.RGB24, false);
            _previousRate = Time.captureFramerate;
            Time.captureFramerate = frameRate;
            Frames = 0;
            Done = false;
        }

        void OnDisable()
        {
            if (_target == null) return;
            Time.captureFramerate = _previousRate;
            _target.Release();
            Destroy(_target);
            Destroy(_image);
            _target = null;
        }

        void LateUpdate()
        {
            if (_target == null) Begin();
            if (Done || source == null) return;
            var previous = source.targetTexture;
            var active = RenderTexture.active;
            source.targetTexture = _target;
            source.Render();
            source.targetTexture = previous;
            RenderTexture.active = _target;
            _image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            _image.Apply();
            RenderTexture.active = active;
            File.WriteAllBytes(Path.Combine(folder, $"{Frames:00000}.png"), _image.EncodeToPNG());
            Frames++;
            if (seconds > 0 && Frames >= Mathf.CeilToInt(seconds * frameRate))
            {
                Done = true;
                enabled = false;
            }
        }
    }
}
