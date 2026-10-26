using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace WiiFitMotionLab
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BalanceLocomotionController : UdonSharpBehaviour
    {
        public const int ModeOff = 0;
        public const int ModeWalking = 1;
        public const int ModeSki = 2;
        public const int ModeSnowboard = 3;

        [Header("Course")]
        public LayerMask snowSurfaceMask = -1;
        public Transform defaultResetPoint;
        public float fallRecoveryHeight = -12f;

        [Header("Ski")]
        public float skiMaximumSpeed = 12f;

        [Header("Snowboard")]
        public float snowboardMaximumSpeed = 10f;

        [Header("Shared physics")]
        public float maximumTurnDegreesPerSecond = 55f;
        public float brakingAcceleration = 6f;
        public float dragPerSecond = 0.35f;
        public float edgeBrakeRatio = 0.35f;
        public float groundProbeDistance = 2.4f;
        public float groundHoldVelocity = 0.8f;

        [Header("Runtime (read only)")]
        public int activeMode;
        public float horizontalInput;
        public float verticalInput;
        public float currentSpeed;
        public bool surfaceDetected;

        private VRCPlayerApi _localPlayer;
        private Transform _activeResetPoint;
        private float _originalWalkSpeed;
        private float _originalRunSpeed;
        private float _originalStrafeSpeed;
        private float _originalJumpImpulse;
        private float _originalGravityStrength;
        private bool _defaultsCaptured;

        private void Start()
        {
            _localPlayer = Networking.LocalPlayer;
            activeMode = ModeOff;
        }

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
        {
            horizontalInput = Mathf.Clamp(value, -1f, 1f);
        }

        public override void InputMoveVertical(float value, UdonInputEventArgs args)
        {
            verticalInput = Mathf.Clamp(value, -1f, 1f);
        }

        public void BeginWalking(Transform resetPoint)
        {
            BeginMode(ModeWalking, resetPoint);
        }

        public void BeginSki(Transform resetPoint)
        {
            BeginMode(ModeSki, resetPoint);
        }

        public void BeginSnowboard(Transform resetPoint)
        {
            BeginMode(ModeSnowboard, resetPoint);
        }

        public void EndCourse()
        {
            RestorePlayerDefaults();
            activeMode = ModeOff;
            horizontalInput = 0f;
            verticalInput = 0f;
            currentSpeed = 0f;
            surfaceDetected = false;
        }

        public void ResetToCourseStart()
        {
            if (!Utilities.IsValid(_localPlayer))
            {
                return;
            }

            Transform resetPoint = Utilities.IsValid(_activeResetPoint) ? _activeResetPoint : defaultResetPoint;
            if (!Utilities.IsValid(resetPoint))
            {
                return;
            }

            _localPlayer.SetVelocity(Vector3.zero);
            _localPlayer.TeleportTo(resetPoint.position, resetPoint.rotation);
        }

        private void BeginMode(int mode, Transform resetPoint)
        {
            if (!Utilities.IsValid(_localPlayer))
            {
                _localPlayer = Networking.LocalPlayer;
            }
            if (!Utilities.IsValid(_localPlayer))
            {
                return;
            }

            CapturePlayerDefaults();
            activeMode = mode;
            _activeResetPoint = resetPoint;
            horizontalInput = 0f;
            verticalInput = 0f;

            if (mode == ModeWalking)
            {
                RestorePlayerDefaults();
                activeMode = ModeWalking;
                return;
            }

            // OSC still reaches the Udon input events, but built-in locomotion must not
            // add a second, sideways velocity while custom ski physics is active.
            _localPlayer.SetWalkSpeed(0f);
            _localPlayer.SetRunSpeed(0f);
            _localPlayer.SetStrafeSpeed(0f);
            _localPlayer.SetJumpImpulse(0f);
            _localPlayer.SetGravityStrength(0f);
        }

        private void LateUpdate()
        {
            if (!Utilities.IsValid(_localPlayer) || activeMode == ModeOff)
            {
                return;
            }

            if (_localPlayer.GetPosition().y < fallRecoveryHeight)
            {
                ResetToCourseStart();
                return;
            }

            if (activeMode == ModeWalking)
            {
                return;
            }

            SimulateSliding(Mathf.Min(Time.deltaTime, 0.05f));
        }

        private void SimulateSliding(float deltaTime)
        {
            Vector3 playerPosition = _localPlayer.GetPosition();
            RaycastHit hit;
            surfaceDetected = Physics.Raycast(
                playerPosition + Vector3.up * 0.35f,
                Vector3.down,
                out hit,
                groundProbeDistance,
                snowSurfaceMask,
                QueryTriggerInteraction.Ignore);

            Vector3 velocity = _localPlayer.GetVelocity();
            if (!surfaceDetected)
            {
                _localPlayer.SetVelocity(velocity + Physics.gravity * deltaTime);
                return;
            }

            Vector3 normal = hit.normal;
            Vector3 planarVelocity = Vector3.ProjectOnPlane(velocity, normal);
            float speed = planarVelocity.magnitude;
            Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, normal).normalized;
            if (downhill.sqrMagnitude < 0.01f)
            {
                downhill = Vector3.ProjectOnPlane(_localPlayer.GetRotation() * Vector3.forward, normal).normalized;
            }

            Vector3 heading = speed > 0.3f ? planarVelocity / speed : downhill;
            float speedRatio = Mathf.Clamp01(speed / Mathf.Max(1f, MaximumSpeed()));
            float turnAuthority = Mathf.Lerp(0.35f, 1f, speedRatio);
            float turnDegrees = horizontalInput * maximumTurnDegreesPerSecond * turnAuthority * deltaTime;
            heading = Quaternion.AngleAxis(turnDegrees, normal) * heading;
            heading = Vector3.ProjectOnPlane(heading, normal).normalized;

            // Gravity accelerates along the slope. Vertical is throttle/brake intent,
            // not a direct forward velocity, so inertia and the hill still matter.
            Vector3 slopeGravity = Vector3.ProjectOnPlane(Physics.gravity, normal);
            float throttle = Mathf.Clamp01(verticalInput);
            float propulsionScale = Mathf.Lerp(0.3f, 1f, throttle);
            Vector3 next = heading * speed;
            next += slopeGravity * propulsionScale * deltaTime;
            next -= next * dragPerSecond * deltaTime;

            float brakeIntent = verticalInput < 0.35f
                ? Mathf.Clamp01((0.35f - verticalInput) / 0.35f)
                : 0f;
            float edgeIntent = horizontalInput * horizontalInput * edgeBrakeRatio;
            float brakeAmount = (brakeIntent + edgeIntent) * brakingAcceleration * deltaTime;
            float nextSpeed = Mathf.Max(0f, next.magnitude - brakeAmount);
            next = next.sqrMagnitude > 0.0001f ? next.normalized * nextSpeed : Vector3.zero;

            float maximumSpeed = MaximumSpeed();
            if (next.magnitude > maximumSpeed)
            {
                next = next.normalized * maximumSpeed;
            }

            currentSpeed = next.magnitude;
            _localPlayer.SetVelocity(next - normal * groundHoldVelocity);
        }

        private float MaximumSpeed()
        {
            return activeMode == ModeSki ? skiMaximumSpeed : snowboardMaximumSpeed;
        }

        private void CapturePlayerDefaults()
        {
            if (_defaultsCaptured)
            {
                return;
            }
            _originalWalkSpeed = _localPlayer.GetWalkSpeed();
            _originalRunSpeed = _localPlayer.GetRunSpeed();
            _originalStrafeSpeed = _localPlayer.GetStrafeSpeed();
            _originalJumpImpulse = _localPlayer.GetJumpImpulse();
            _originalGravityStrength = _localPlayer.GetGravityStrength();
            _defaultsCaptured = true;
        }

        private void RestorePlayerDefaults()
        {
            if (!Utilities.IsValid(_localPlayer) || !_defaultsCaptured)
            {
                return;
            }
            _localPlayer.SetWalkSpeed(_originalWalkSpeed);
            _localPlayer.SetRunSpeed(_originalRunSpeed);
            _localPlayer.SetStrafeSpeed(_originalStrafeSpeed);
            _localPlayer.SetJumpImpulse(_originalJumpImpulse);
            _localPlayer.SetGravityStrength(_originalGravityStrength);
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (player.isLocal)
            {
                EndCourse();
            }
        }

        private void OnDisable()
        {
            EndCourse();
        }
    }
}
