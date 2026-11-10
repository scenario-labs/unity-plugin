using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Scenario.Editor
{
    /// <summary>Shared project settings, committed with the project (ProjectSettings/ScenarioSettings.asset).</summary>
    [FilePath("ProjectSettings/ScenarioSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ScenarioProjectSettings : ScriptableSingleton<ScenarioProjectSettings>
    {
        [Tooltip("Where results are saved, inside Assets.")]
        public string OutputRoot = "Assets/Scenario";
        [Tooltip("Put each day's results in a dated folder (Assets/Scenario/Image/2026-10-18).")]
        public bool DatedFolders = true;
        [Tooltip("Ask before any request above this many Creative Units.")]
        public int ConfirmAboveCu = 1000;
        [Tooltip("Sprites instead of plain textures for images (2D projects).")]
        public bool ImagesAsSprites;
        [Tooltip("Sprite pixels per unit for imported images.")]
        public float PixelsPerUnit = 100;
        [Tooltip("Point for pixel art, bilinear otherwise.")]
        public FilterMode ImageFilter = FilterMode.Bilinear;
        [Tooltip("Add a MeshCollider to placed 3D models.")]
        public bool AddCollider;
        [Tooltip("Frame placed objects in the Scene view.")]
        public bool FrameAfterPlace = true;

        public void Save() => Save(true);
    }
}
