// Designed by KINEMATION, 2023

using System;
using KINEMATION.FPSAnimationFramework.Runtime.Camera;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.IkMotionLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;
using KINEMATION.Shared.KAnimationCore.Runtime.Core;
using KINEMATION.Shared.KAnimationCore.Runtime.Input;

using UnityEngine;
using System.Collections.Generic;
using Random = UnityEngine.Random;

namespace Demo.Scripts.Runtime
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public class TabAttribute : PropertyAttribute
    {
        public readonly string tabName;

        public TabAttribute(string tabName)
        {
            this.tabName = tabName;
        }
    }
    
    public enum FPSAimState
    {
        None,
        Ready,
        Aiming,
        PointAiming
    }

    public enum FPSActionState
    {
        None,
        Reloading,
        WeaponChange
    }

    // An example-controller class
    [DefaultExecutionOrder(100)]
    public class FPSController : MonoBehaviour
    {
        [Tab("Animation")] 
        [Header("General")] 
        [SerializeField] private Animator animator;

        [Header("Turn In Place")]
        [SerializeField] private float turnInPlaceAngle;
        [SerializeField] private AnimationCurve turnCurve = new AnimationCurve(new Keyframe(0f, 0f));
        [SerializeField] private float turnSpeed = 1f;

        [Header("Leaning")] 
        [SerializeField] private float smoothLeanStep = 1f;
        [SerializeField, Range(0f, 1f)] private float startLean = 1f;
        
        [Header("Dynamic Motions")]
        [SerializeField] private IkMotionLayerSettings aimMotionAsset;
        [SerializeField] private IkMotionLayerSettings leanMotionAsset;
        [SerializeField] private IkMotionLayerSettings crouchMotionAsset;
        [SerializeField] private IkMotionLayerSettings unCrouchMotionAsset;
        [SerializeField] private IkMotionLayerSettings onJumpMotionAsset;
        [SerializeField] private IkMotionLayerSettings onLandedMotionAsset;
        [SerializeField] private IkMotionLayerSettings onStartStopMoving;
        
        [SerializeField] private FPSAnimationAsset unequipClip;
        
        [Header("General")] 
        [Tab("Controller")]
        [SerializeField] private float timeScale = 1f;
        [SerializeField, Min(0f)] private float equipDelay = 0f;

        [Header("Camera")]
        [SerializeField] private Transform mainCamera;
        [SerializeField] private Transform cameraHolder;
        [SerializeField] private Transform firstPersonCamera;
        [SerializeField] private float hipFov = 95f;
        [SerializeField] private float aimFov = 80f;
        [SerializeField] private float sensitivity;
        [SerializeField] private Vector2 freeLookAngle;

        [Header("Movement")] 
        [SerializeField] private FPSMovement movementComponent;
        
        [SerializeField] [Tab("Weapon")] 
        private List<Weapon> weapons;
        private Vector2 _playerInput;

        // Used for free-look
        private Vector2 _freeLookInput;

        private int _currentWeaponIndex;
        private int _lastIndex;
        
        private int _bursts;
        private bool _freeLook;
        
        private FPSAimState aimState;
        private FPSActionState actionState;
        
        private static readonly int Crouching = Animator.StringToHash("Crouching");
        private static readonly int OverlayType = Animator.StringToHash("OverlayType");
        private static readonly int TurnRight = Animator.StringToHash("TurnRight");
        private static readonly int TurnLeft = Animator.StringToHash("TurnLeft");
        private static readonly int Sliding = Animator.StringToHash("Sliding");

        private FPSAnimator _fpsAnimator;
        private UserInputController _animationInput;
        private FPSCameraController _cameraController;
        private bool _updateCamera;
        private RecoilAnimation _recoil;
        private float _leanInput;
        private Vector2 _mouseDelta;

        private Vector2 _controllerRecoil;
        private float _recoilStep;
        private bool _isFiring;

        private float _lastRecoilTime;

        private bool HasActiveAction()
        {
            return actionState != FPSActionState.None;
        }

        private bool IsAiming()
        {
            return aimState is FPSAimState.Aiming or FPSAimState.PointAiming;
        }

        private void Start()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
            if (animator == null || mainCamera == null ||
                !animator.TryGetComponent(out _fpsAnimator) ||
                !animator.TryGetComponent(out _animationInput) ||
                !animator.TryGetComponent<FPSBoneController>(out _) ||
                !animator.TryGetComponent<FPSPlayablesController>(out _) ||
                !animator.TryGetComponent(out _recoil) ||
                !mainCamera.TryGetComponent(out _cameraController))
            {
                Debug.LogWarning("The demo player requires the FPS Animation Framework animator and camera components.", this);
                enabled = false;
                return;
            }

            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
            moveRotation = transform.rotation;

            _updateCamera = animator.GetComponentInChildren<FPSCameraController>() != _cameraController;
            _fpsAnimator.Initialize();
            if (_updateCamera) _cameraController.Initialize();
            SetAimInput(false);

            movementComponent = GetComponent<FPSMovement>();

            movementComponent.onStartMoving.AddListener(OnMoveStarted);
            movementComponent.onStopMoving.AddListener(OnMoveEnded);

            movementComponent.onCrouch.AddListener(OnCrouch);
            movementComponent.onUncrouch.AddListener(OnUncrouch);

            movementComponent.onJump.AddListener(OnJump);
            movementComponent.onLanded.AddListener(OnLand);

            movementComponent.onSprintStarted.AddListener(OnSprintStarted);
            movementComponent.onSprintEnded.AddListener(OnSprintEnded);

            movementComponent.onSlideStarted.AddListener(OnSlideStarted);
            movementComponent.onSlideEnded.AddListener(OnSlideEnded);

            movementComponent.onProneStarted.AddListener(() => _animationInput.SetValue("CollisionWeight", 0f));
            movementComponent.onProneEnded.AddListener(() =>
            {
                _animationInput.SetValue("CollisionWeight", 1f);
                _animationInput.SetValue("PronePoseWeight", 0f);
            });

            movementComponent.slideCondition += () => !HasActiveAction();
            movementComponent.sprintCondition += () => !HasActiveAction();
            movementComponent.proneCondition += () => !HasActiveAction();

            actionState = FPSActionState.None;

            EquipWeapon();
        }

        private void UnequipWeapon()
        {
            DisableAim();

            actionState = FPSActionState.WeaponChange;
            if (unequipClip != null) _fpsAnimator.playablesController.PlayAnimation(unequipClip);
        }

        public void ResetActionState()
        {
            actionState = FPSActionState.None;
        }

        public void RefreshStagedState()
        {
        }
        
        public void ResetStagedState()
        {
        }

        private void EquipWeapon()
        {
            if (weapons.Count == 0) return;

            weapons[_lastIndex].gameObject.SetActive(false);
            var gun = weapons[_currentWeaponIndex];

            _bursts = gun.burstAmount;
            
            gun.gameObject.SetActive(true);
            gun.UpdateAimPoint(false, false);
            _fpsAnimator.LinkAnimatorProfile(gun.gameObject);
            if (gun.overlayPose != null) _fpsAnimator.playablesController.PlayPose(gun.overlayPose);
            _recoil.Init(gun.recoilData, Mathf.Max(gun.fireRate, 1f), gun.fireMode);

            animator.SetFloat(OverlayType, (float) gun.overlayType);
            actionState = FPSActionState.None;
        }

        private void DisableAim()
        {
            aimState = FPSAimState.None;
            SetAimInput(false);
            GetGun()?.UpdateAimPoint(false, false);
        }

        private void SetAimInput(bool isAiming)
        {
            _animationInput.SetValue(FPSANames.IsAiming, isAiming);
            _animationInput.SetValue(FPSANames.UseFreeAim, !isAiming);
            _animationInput.SetValue("SwayWeight", isAiming ? 0.5f : 1f);
            _recoil.isAiming = isAiming;
            _cameraController.UpdateTargetFOV(isAiming ? aimFov : hipFov);
        }

        public void ToggleAim()
        {
            if (GetGun()?.canAim != true || HasActiveAction()) return;
            
            PlayMotion(aimMotionAsset);
            
            if (IsAiming())
            {
                DisableAim();
                return;
            }

            aimState = FPSAimState.Aiming;
            SetAimInput(true);
        }

        public void ChangeScope()
        {
            GetGun()?.UpdateAimPoint(true, aimState == FPSAimState.PointAiming);
        }

        private void PlayMotion(IkMotionLayerSettings motion)
        {
            if (motion != null) _fpsAnimator.LinkAnimatorLayer(motion);
        }

        private void Fire()
        {
            var gun = GetGun();
            if (gun == null || HasActiveAction()) return;
            
            gun.OnFire();
            if (gun.fireClip != null) _fpsAnimator.playablesController.PlayAnimation(gun.fireClip);
            _cameraController.PlayCameraShake(gun.cameraShake);
            
            if (gun.recoilPattern != null)
            {
                float aimRatio = IsAiming() ? gun.recoilPattern.aimRatio : 1f;
                float hRecoil = Random.Range(gun.recoilPattern.horizontalVariation.x,
                    gun.recoilPattern.horizontalVariation.y);
                _controllerRecoil += new Vector2(hRecoil, _recoilStep) * aimRatio;
            }
            
            _recoil.Play();

            if (gun.fireMode == FireMode.Burst && _bursts-- <= 0)
            {
                OnFireReleased();
                return;
            }
            
            if (gun.fireMode == FireMode.Semi)
            {
                _isFiring = false;
                return;
            }
            
            Invoke(nameof(Fire), 60f / gun.fireRate);
            if (gun.recoilPattern != null) _recoilStep += gun.recoilPattern.acceleration;
        }

        private void OnFirePressed()
        {
            if (weapons.Count == 0 || HasActiveAction()) return;

            if (Mathf.Approximately(GetGun().fireRate, 0f))
            {
                return;
            }

            if (Time.unscaledTime - _lastRecoilTime < 60f / GetGun().fireRate)
            {
                return;
            }

            _lastRecoilTime = Time.unscaledTime;
            _bursts = GetGun().burstAmount - 1;

            if (GetGun().recoilPattern != null)
            {
                _recoilStep = GetGun().recoilPattern.step;
            }
            
            _isFiring = true;
            Fire();
        }

        private Weapon GetGun()
        {
            if (weapons.Count == 0) return null;
            
            return weapons[_currentWeaponIndex];
        }

        private void OnFireReleased()
        {
            if (weapons.Count == 0) return;

            _recoil.Stop();

            _recoilStep = 0f;
            _isFiring = false;
            CancelInvoke(nameof(Fire));
        }

        private void OnMoveStarted()
        {
            PlayMotion(onStartStopMoving);

            if (movementComponent.PoseState == FPSPoseState.Prone)
            {
                _animationInput.SetValue("PronePoseWeight", 1f);
            }
        }

        private void OnMoveEnded()
        {
            PlayMotion(onStartStopMoving);
            _animationInput.SetValue("PronePoseWeight", 0f);
        }

        private void OnSlideStarted()
        {
            _animationInput.SetValue("LookLayerWeight", 0.3f);
            if (animator.HasState(0, Sliding)) animator.CrossFade(Sliding, 0.1f);
        }

        private void OnSlideEnded()
        {
            _animationInput.SetValue("LookLayerWeight", 1f);
        }

        private void OnSprintStarted()
        {
            OnFireReleased();
            DisableAim();
            
            _animationInput.SetValue("LookLayerWeight", 0.5f);
            if (GetGun()?.overlayType == Runtime.OverlayType.Rifle)
            {
                _animationInput.SetValue("SprintPoseWeight", 1f);
            }
        }

        private void OnSprintEnded()
        {
            _animationInput.SetValue("LookLayerWeight", 1f);
            _animationInput.SetValue("SprintPoseWeight", 0f);
        }

        private void OnCrouch()
        {
            animator.SetBool(Crouching, true);
            PlayMotion(crouchMotionAsset);
        }

        private void OnUncrouch()
        {
            animator.SetBool(Crouching, false);
            PlayMotion(unCrouchMotionAsset);
        }

        private void OnJump()
        {
            PlayMotion(onJumpMotionAsset);
        }

        private void OnLand()
        {
            PlayMotion(onLandedMotionAsset);
        }

        private void TryReload()
        {
            if (HasActiveAction()) return;

            var reloadClip = GetGun()?.reloadClip;

            if (reloadClip == null) return;
            
            OnFireReleased();
            
            if (!_fpsAnimator.playablesController.PlayAnimation(reloadClip)) return;
            GetGun().Reload();
            actionState = FPSActionState.Reloading;
        }

        private void TryGrenadeThrow()
        {
            if (HasActiveAction()) return;
            var grenadeClip = GetGun()?.grenadeClip;
            if (grenadeClip == null) return;
            
            OnFireReleased();
            DisableAim();
            if (!_fpsAnimator.playablesController.PlayAnimation(grenadeClip)) return;
            actionState = FPSActionState.Reloading;
        }

        private bool _isLeaning;
        
        private void ChangeWeapon_Internal(int newIndex)
        {
            if (newIndex == _currentWeaponIndex || newIndex > weapons.Count - 1)
            {
                return;
            }
            
            OnFireReleased();
            UnequipWeapon();

            _lastIndex = _currentWeaponIndex;
            _currentWeaponIndex = newIndex;
            Invoke(nameof(EquipWeapon), equipDelay);
        }

        private void HandleWeaponChangeInput()
        {
            if (movementComponent.PoseState == FPSPoseState.Prone) return;
            if (HasActiveAction() || weapons.Count == 0) return;
            
            if (Input.GetKeyDown(KeyCode.F))
            {
                ChangeWeapon_Internal(_currentWeaponIndex + 1 > weapons.Count - 1 ? 0 : _currentWeaponIndex + 1);
                return;
            }
            
            for (int i = (int) KeyCode.Alpha1; i <= (int) KeyCode.Alpha9; i++)
            {
                if (Input.GetKeyDown((KeyCode) i))
                {
                    ChangeWeapon_Internal(i - (int) KeyCode.Alpha1);
                }
            }
        }

        private void UpdateActionInput()
        {
            if (movementComponent.MovementState == FPSMovementState.Sprinting)
            {
                return;
            }
            
            if (Input.GetKeyDown(KeyCode.R))
            {
                TryReload();
            }

            if (Input.GetKeyDown(KeyCode.G))
            {
                TryGrenadeThrow();
            }

            HandleWeaponChangeInput();
            
            if (aimState != FPSAimState.Ready)
            {
                bool wasLeaning = _isLeaning;
                
                bool rightLean = Input.GetKey(KeyCode.E);
                bool leftLean = Input.GetKey(KeyCode.Q);

                _isLeaning = rightLean || leftLean;
                
                if (_isLeaning != wasLeaning)
                {
                    PlayMotion(leanMotionAsset);
                    _leanInput = wasLeaning ? 0f : rightLean ? -startLean : startLean;
                }

                if (_isLeaning)
                {
                    float leanValue = Input.GetAxis("Mouse ScrollWheel") * smoothLeanStep;
                    _leanInput = Mathf.Clamp(_leanInput + leanValue, -1f, 1f);
                }

                if (Input.GetKeyDown(KeyCode.Mouse0))
                {
                    OnFirePressed();
                }

                if (Input.GetKeyUp(KeyCode.Mouse0))
                {
                    OnFireReleased();
                }

                if (Input.GetKeyDown(KeyCode.Mouse1))
                {
                    ToggleAim();
                }

                if (Input.GetKeyDown(KeyCode.V))
                {
                    ChangeScope();
                }

                if (Input.GetKeyDown(KeyCode.B) && IsAiming())
                {
                    aimState = aimState == FPSAimState.PointAiming ? FPSAimState.Aiming : FPSAimState.PointAiming;
                    GetGun()?.UpdateAimPoint(false, aimState == FPSAimState.PointAiming);
                }
            }
            
            if (Input.GetKeyDown(KeyCode.H))
            {
                if (aimState == FPSAimState.Ready)
                {
                    aimState = FPSAimState.None;
                    _animationInput.SetValue("LookLayerWeight", 1f);
                }
                else
                {
                    DisableAim();
                    aimState = FPSAimState.Ready;
                    _animationInput.SetValue("LookLayerWeight", 0.5f);
                    OnFireReleased();
                }
            }
        }

        private Quaternion moveRotation;
        private float turnProgress = 1f;
        private bool isTurning = false;

        private void TurnInPlace()
        {
            float turnInput = _playerInput.x;
            _playerInput.x = Mathf.Clamp(_playerInput.x, -90f, 90f);
            turnInput -= _playerInput.x;

            float sign = Mathf.Sign(_playerInput.x);
            if (Mathf.Abs(_playerInput.x) > turnInPlaceAngle)
            {
                if (!isTurning)
                {
                    turnProgress = 0f;
                    
                    animator.ResetTrigger(TurnRight);
                    animator.ResetTrigger(TurnLeft);
                    
                    animator.SetTrigger(sign > 0f ? TurnRight : TurnLeft);
                }
                
                isTurning = true;
            }

            transform.rotation *= Quaternion.Euler(0f, turnInput, 0f);
            
            float lastProgress = turnCurve.Evaluate(turnProgress);
            turnProgress += Time.deltaTime * turnSpeed;
            turnProgress = Mathf.Min(turnProgress, 1f);
            
            float deltaProgress = turnCurve.Evaluate(turnProgress) - lastProgress;

            _playerInput.x -= sign * turnInPlaceAngle * deltaProgress;
            
            transform.rotation *= Quaternion.Slerp(Quaternion.identity,
                Quaternion.Euler(0f, sign * turnInPlaceAngle, 0f), deltaProgress);
            
            if (Mathf.Approximately(turnProgress, 1f) && isTurning)
            {
                isTurning = false;
            }
        }

        private float _jumpState = 0f;

        private void UpdateLookInput()
        {
            _freeLook = Input.GetKey(KeyCode.X);
            _mouseDelta = Vector2.zero;

            float deltaMouseX = Input.GetAxis("Mouse X") * sensitivity;
            float deltaMouseY = -Input.GetAxis("Mouse Y") * sensitivity;
            
            if (_freeLook)
            {
                // No input for both controller and animation component. We only want to rotate the camera

                _freeLookInput.x += deltaMouseX;
                _freeLookInput.y += deltaMouseY;

                _freeLookInput.x = Mathf.Clamp(_freeLookInput.x, -freeLookAngle.x, freeLookAngle.x);
                _freeLookInput.y = Mathf.Clamp(_freeLookInput.y, -freeLookAngle.y, freeLookAngle.y);

                return;
            }

            _freeLookInput = Vector2.Lerp(_freeLookInput, Vector2.zero, 
                KMath.ExpDecayAlpha(15f, Time.deltaTime));
            
            _playerInput.x += deltaMouseX;
            _playerInput.y += deltaMouseY;
            
            float proneWeight = animator.GetFloat("ProneWeight");
            Vector2 pitchClamp = Vector2.Lerp(new Vector2(-90f, 90f), new Vector2(-30, 0f), proneWeight);
            
            _playerInput.y = Mathf.Clamp(_playerInput.y, pitchClamp.x, pitchClamp.y);
            moveRotation *= Quaternion.Euler(0f, deltaMouseX, 0f);
            TurnInPlace();

            _jumpState = Mathf.Lerp(_jumpState, movementComponent.IsInAir() ? 1f : 0f,
                KMath.ExpDecayAlpha(10f, Time.deltaTime));

            float moveWeight = Mathf.Clamp01(movementComponent.AnimatorVelocity.magnitude);
            transform.rotation = Quaternion.Slerp(transform.rotation, moveRotation, moveWeight);
            transform.rotation = Quaternion.Slerp(transform.rotation, moveRotation, _jumpState);
            _playerInput.x *= 1f - moveWeight;
            _playerInput.x *= 1f - _jumpState;

            _mouseDelta = new Vector2(deltaMouseX, deltaMouseY);
            _recoil.UpdateDeltaInput(_mouseDelta);
        }

        private Vector2 _cameraRecoilOffset;

        private void UpdateRecoil()
        {
            if (Mathf.Approximately(_controllerRecoil.magnitude, 0f)
                && Mathf.Approximately(_cameraRecoilOffset.magnitude, 0f))
            {
                return;
            }
            
            float smoothing = 8f;
            float restoreSpeed = 8f;
            float cameraWeight = 0f;

            var pattern = GetGun()?.recoilPattern;
            if (pattern != null)
            {
                smoothing = pattern.smoothing;
                restoreSpeed = pattern.cameraRestoreSpeed;
                cameraWeight = pattern.cameraWeight;
            }
            
            _controllerRecoil = Vector2.Lerp(_controllerRecoil, Vector2.zero,
                KMath.ExpDecayAlpha(smoothing, Time.deltaTime));

            _playerInput += _controllerRecoil * Time.deltaTime;
            
            Vector2 clamp = Vector2.Lerp(Vector2.zero, new Vector2(90f, 90f), cameraWeight);
            _cameraRecoilOffset -= _controllerRecoil * Time.deltaTime;
            _cameraRecoilOffset = Vector2.ClampMagnitude(_cameraRecoilOffset, clamp.magnitude);

            if (_isFiring) return;

            _cameraRecoilOffset = Vector2.Lerp(_cameraRecoilOffset, Vector2.zero,
                KMath.ExpDecayAlpha(restoreSpeed, Time.deltaTime));
        }

        public void OnWarpStarted()
        {
            movementComponent.enabled = false;
            GetComponent<CharacterController>().enabled = false;
        }

        public void OnWarpEnded()
        {
            movementComponent.enabled = true;
            GetComponent<CharacterController>().enabled = true;
        }

        private void Update()
        {
            Time.timeScale = timeScale;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Application.Quit(0);
            }
            
            UpdateActionInput();
            UpdateLookInput();
            UpdateRecoil();

            _animationInput.SetValue(FPSANames.MouseInput, (Vector4) _playerInput);
            _animationInput.SetValue(FPSANames.MouseDeltaInput, (Vector4) _mouseDelta);
            _animationInput.SetValue(FPSANames.MoveInput, (Vector4) movementComponent.AnimatorVelocity);
            _animationInput.SetValue(FPSANames.LeanInput, _leanInput);
        }

        private void LateUpdate()
        {
            UpdateCameraRotation();
        }
        
        public void UpdateCameraRotation()
        {
            if (_updateCamera) _cameraController.UpdateCamera();
            var rotation = mainCamera.rotation;
            cameraHolder.position = firstPersonCamera.position;
            mainCamera.rotation = Quaternion.AngleAxis(_playerInput.x + _cameraRecoilOffset.x, transform.up)
                * rotation * Quaternion.Euler(_freeLookInput.y + _cameraRecoilOffset.y, _freeLookInput.x, 0f);
        }
    }
}
