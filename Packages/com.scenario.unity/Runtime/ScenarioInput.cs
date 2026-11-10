using UnityEngine;
#if SCENARIO_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Scenario
{
    /// <summary>
    /// Keyboard, mouse and gamepad for the playable character, through the Input System
    /// when the project uses it, else the legacy Input Manager.
    /// </summary>
    public static class ScenarioInput
    {
        /// <summary>WASD or arrows, or the left stick.</summary>
        public static Vector2 Move()
        {
#if SCENARIO_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var value = Vector2.zero;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) value.y += 1;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) value.y -= 1;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) value.x += 1;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) value.x -= 1;
            }
            if (Gamepad.current != null) value += Gamepad.current.leftStick.ReadValue();
            return Vector2.ClampMagnitude(value, 1);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Vector2.ClampMagnitude(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1);
#else
            return Vector2.zero;
#endif
        }

        /// <summary>Shift, or the left stick button.</summary>
        public static bool Run()
        {
#if SCENARIO_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.shiftKey.isPressed
                || Gamepad.current != null && Gamepad.current.leftStickButton.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#else
            return false;
#endif
        }

        /// <summary>Space, or the gamepad's south button, this frame.</summary>
        public static bool Jump()
        {
#if SCENARIO_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame
                || Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Space);
#else
            return false;
#endif
        }

        /// <summary>Camera orbit in degrees this frame: the mouse with the right button held, or the right stick.</summary>
        public static Vector2 Look()
        {
#if SCENARIO_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var value = Vector2.zero;
            if (Mouse.current != null && Mouse.current.rightButton.isPressed) value += Mouse.current.delta.ReadValue() * 0.15f;
            if (Gamepad.current != null) value += Gamepad.current.rightStick.ReadValue() * (120f * Time.deltaTime);
            return value;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButton(1) ? new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 3f : Vector2.zero;
#else
            return Vector2.zero;
#endif
        }
    }
}
